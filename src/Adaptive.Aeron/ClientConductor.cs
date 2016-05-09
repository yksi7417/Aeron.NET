﻿using System;
using System.Collections.Generic;
using System.Threading;
using Adaptive.Aeron.Exceptions;
using Adaptive.Agrona;
using Adaptive.Agrona.Concurrent;
using Adaptive.Agrona.Concurrent.Broadcast;
using Adaptive.Agrona.Concurrent.Status;
using Adaptive.Agrona.Util;

namespace Adaptive.Aeron
{
    /// <summary>
    /// Client conductor takes responses and notifications from media driver and acts on them.
    /// As well as passes commands to the media driver.
    /// </summary>
    internal class ClientConductor : IAgent, IDriverListener
    {
        private const long NO_CORRELATION_ID = -1;
        private static readonly long RESOURCE_TIMEOUT_NS = NanoUtil.FromSeconds(1);
        private static readonly long RESOURCE_LINGER_NS = NanoUtil.FromSeconds(5);

        private readonly long _keepAliveIntervalNs;
        private readonly long _driverTimeoutMs;
        private readonly long _driverTimeoutNs;
        private readonly long _interServiceTimeoutNs; 
        private readonly long _publicationConnectionTimeoutMs;
        private long _timeOfLastKeepalive;
        private long _timeOfLastCheckResources;
        private long _timeOfLastWork;
        private volatile bool _driverActive = true;

        private readonly IEpochClock _epochClock;
        private readonly INanoClock _nanoClock;
        private readonly DriverListenerAdapter _driverListener;
        private readonly ILogBuffersFactory _logBuffersFactory;
        private readonly ActivePublications _activePublications = new ActivePublications();
        private readonly ActiveSubscriptions _activeSubscriptions = new ActiveSubscriptions();
        private readonly List<IManagedResource> _lingeringResources = new List<IManagedResource>();
        private readonly UnsafeBuffer _counterValuesBuffer;
        private readonly DriverProxy _driverProxy;
        private readonly ErrorHandler _errorHandler;
        private readonly AvailableImageHandler _availableImageHandler;
        private readonly UnavailableImageHandler _unavailableImageHandler;

        private RegistrationException _driverException;

        internal ClientConductor(
            IEpochClock epochClock,
            INanoClock nanoClock,
            CopyBroadcastReceiver broadcastReceiver, 
            ILogBuffersFactory logBuffersFactory, 
            UnsafeBuffer counterValuesBuffer, 
            DriverProxy driverProxy, 
            ErrorHandler errorHandler, 
            AvailableImageHandler availableImageHandler, 
            UnavailableImageHandler unavailableImageHandler, 
            long keepAliveIntervalNs, 
            long driverTimeoutMs, 
            long interServiceTimeoutNs, 
            long publicationConnectionTimeoutMs)
        {
            _epochClock = epochClock;
            _nanoClock = nanoClock;
            _timeOfLastKeepalive = nanoClock.NanoTime();
            _timeOfLastCheckResources = nanoClock.NanoTime();
            _timeOfLastWork = nanoClock.NanoTime();
            _errorHandler = errorHandler;
            _counterValuesBuffer = counterValuesBuffer;
            _driverProxy = driverProxy;
            _logBuffersFactory = logBuffersFactory;
            _availableImageHandler = availableImageHandler;
            _unavailableImageHandler = unavailableImageHandler;
            _keepAliveIntervalNs = keepAliveIntervalNs;
            _driverTimeoutMs = driverTimeoutMs;
            _driverTimeoutNs = NanoUtil.FromMilliseconds(driverTimeoutMs);
            _interServiceTimeoutNs = interServiceTimeoutNs;
            _publicationConnectionTimeoutMs = publicationConnectionTimeoutMs;

            _driverListener = new DriverListenerAdapter(broadcastReceiver, this);
        }

        public void OnClose()
        {
            lock (this)
            {
                _activePublications.Dispose();
                _activeSubscriptions.Dispose();

                Thread.Yield();

                _lingeringResources.ForEach(mr => mr.Delete());
            }
        }

        public int DoWork()
        {
            lock (this)
            {
                return DoWork(NO_CORRELATION_ID, null);
            }
        }

        public string RoleName()
        {
            return "client-conductor";
        }

        internal Publication AddPublication(string channel, int streamId)
        {
            lock (this)
            {
                VerifyDriverIsActive();

                Publication publication = _activePublications.Get(channel, streamId);
                if (publication == null)
                {
                    long correlationId = _driverProxy.AddPublication(channel, streamId);
                    long timeout = _nanoClock.NanoTime() + _driverTimeoutNs;

                    DoWorkUntil(correlationId, timeout, channel);

                    publication = _activePublications.Get(channel, streamId);
                }

                publication.IncRef();

                return publication;
            }
        }

        internal void ReleasePublication(Publication publication)
        {
            lock (this)
            {
                VerifyDriverIsActive();

                if (publication == _activePublications.Remove(publication.Channel(), publication.StreamId()))
                {
                    long correlationId = _driverProxy.RemovePublication(publication.RegistrationId());

                    long timeout = _nanoClock.NanoTime() + _driverTimeoutNs;

                    LingerResource(publication.ManagedResource());
                    DoWorkUntil(correlationId, timeout, publication.Channel());
                }
            }
        }

        internal Subscription AddSubscription(string channel, int streamId)
        {
            lock (this)
            {
                VerifyDriverIsActive();

                long correlationId = _driverProxy.AddSubscription(channel, streamId);
                long timeout = _nanoClock.NanoTime() + _driverTimeoutNs;

                Subscription subscription = new Subscription(this, channel, streamId, correlationId);
                _activeSubscriptions.Add(subscription);

                DoWorkUntil(correlationId, timeout, channel);

                return subscription;
            }
        }

        internal void ReleaseSubscription(Subscription subscription)
        {
            lock (this)
            {
                VerifyDriverIsActive();

                long correlationId = _driverProxy.RemoveSubscription(subscription.RegistrationId());
                long timeout = _nanoClock.NanoTime() + _driverTimeoutNs;

                DoWorkUntil(correlationId, timeout, subscription.Channel());

                _activeSubscriptions.Remove(subscription);
            }
        }

        public void OnNewPublication(string channel, int streamId, int sessionId, int publicationLimitId, string logFileName, long correlationId)
        {
            Publication publication = new Publication(this, channel, streamId, sessionId, new UnsafeBufferPosition(_counterValuesBuffer, publicationLimitId), _logBuffersFactory.Map(logFileName), correlationId);

            _activePublications.Put(channel, streamId, publication);
        }

        public void OnAvailableImage(int streamId, int sessionId, IDictionary<long, long> subscriberPositionMap, string logFileName, string sourceIdentity, long correlationId)
        {
            _activeSubscriptions.ForEach(streamId, (subscription) =>
            {
                if (!subscription.HasImage(sessionId))
                {
                    long positionId = subscriberPositionMap[subscription.RegistrationId()];
                    if (Adaptive.Aeron.DriverListenerAdapter.MISSING_REGISTRATION_ID != positionId)
                    {
                        var image = new Image(subscription, sessionId, new UnsafeBufferPosition(_counterValuesBuffer, (int)positionId), _logBuffersFactory.Map(logFileName), _errorHandler, sourceIdentity, correlationId);
                        subscription.AddImage(image);
                        _availableImageHandler(image);
                    }
                }
            });
        }

        public void OnError(ErrorCode errorCode, string message, long correlationId)
        {
            _driverException = new RegistrationException(errorCode, message);
        }

        public void OnUnavailableImage(int streamId, long correlationId)
        {
            _activeSubscriptions.ForEach(streamId, (subscription) =>
            {
                var image = subscription.RemoveImage(correlationId);
                if (null != image)
                {
                    _unavailableImageHandler(image);
                }
            });
        }

        internal DriverListenerAdapter DriverListenerAdapter()
        {
            return _driverListener;
        }

        internal void LingerResource(IManagedResource managedResource)
        {
            managedResource.TimeOfLastStateChange(_nanoClock.NanoTime());
            _lingeringResources.Add(managedResource);
        }

        internal bool IsPublicationConnected(long timeOfLastStatusMessage)
        {
            return (_epochClock.Time() <= (timeOfLastStatusMessage + _publicationConnectionTimeoutMs));
        }

        internal UnavailableImageHandler UnavailableImageHandler()
        {
            return _unavailableImageHandler;
        }

        private void CheckDriverHeartbeat()
        {
            long now = _epochClock.Time();
            long currentDriverKeepaliveTime = _driverProxy.TimeOfLastDriverKeepalive();

            if (_driverActive && (now > (currentDriverKeepaliveTime + _driverTimeoutMs)))
            {
                _driverActive = false;

                string msg = $"Driver has been inactive for over {_driverTimeoutMs:D}ms";
                _errorHandler(new DriverTimeoutException(msg));
            }
        }

        private void VerifyDriverIsActive()
        {
            if (!_driverActive)
            {
                throw new DriverTimeoutException("Driver is inactive");
            }
        }

        private int DoWork(long correlationId, string expectedChannel)
        {
            int workCount = 0;

            try
            {
                workCount += OnCheckTimeouts();
                workCount += _driverListener.PollMessage(correlationId, expectedChannel);
            }
            catch (Exception ex)
            {
                _errorHandler(ex);
            }

            return workCount;
        }

        private void DoWorkUntil(long correlationId, long timeout, string expectedChannel)
        {
            _driverException = null;

            do
            {
                DoWork(correlationId, expectedChannel);

                if (_driverListener.LastReceivedCorrelationId() == correlationId)
                {
                    if (null != _driverException)
                    {
                        throw _driverException;
                    }

                    return;
                }
            } while (_nanoClock.NanoTime() < timeout);

            throw new DriverTimeoutException("No response from driver within timeout");
        }

        private int OnCheckTimeouts()
        {
            long now = _nanoClock.NanoTime();
            int result = 0;

            if (now > (_timeOfLastWork + _interServiceTimeoutNs))
            {
                OnClose();

                throw new ConductorServiceTimeoutException(
                    $"Timeout between service calls over {_interServiceTimeoutNs:D}ns");
            }

            _timeOfLastWork = now;

            if (now > (_timeOfLastKeepalive + _keepAliveIntervalNs))
            {
                _driverProxy.SendClientKeepalive();
                CheckDriverHeartbeat();

                _timeOfLastKeepalive = now;
                result++;
            }

            if (now > (_timeOfLastCheckResources + RESOURCE_TIMEOUT_NS))
            {
                for (int i = _lingeringResources.Count - 1; i >= 0; i--)
                {
                    var resource = _lingeringResources[i];
                    if (now > (resource.TimeOfLastStateChange() + RESOURCE_LINGER_NS))
                    {
                        _lingeringResources.RemoveAt(i);
                        resource.Delete();
                    }
                }

                _timeOfLastCheckResources = now;
                result++;
            }

            return result;
        }
    }
}