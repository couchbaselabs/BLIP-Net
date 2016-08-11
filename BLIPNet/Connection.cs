//
// Connection.cs
//
// Author:
//  Jim Borden  <jim.borden@couchbase.com>
//
// Copyright (c) 2016 Couchbase, Inc All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Dispatch;
using BLIP.Util;

namespace BLIP
{
    public sealed class RequestWrapper
    {
        public readonly Request Request;
        public bool Handled;

        internal RequestWrapper(Request request)
        {
            Request = request;
        }
    }

    public abstract class Connection
    {
        private static readonly string Tag = typeof(Connection).Name;
        private static readonly string[] TypeStrings = { "MSG", "RPY", "ERR", "3??", "ACKMSG", "ACKRPY", "6??", "7??" };
        private const int DefaultFrameSize = 4096;

        private SerialQueue _transportScheduler = new SerialQueue();
        private bool _transportIsOpen;
        private SerialQueue _callbackScheduler = new SerialQueue();
        private Dictionary<ulong, Request> _pendingRequests = new Dictionary<ulong, Request>();
        private Dictionary<ulong, Response> _pendingResponses = new Dictionary<ulong, Response>();
        private Dictionary<string, Action<Request>> _registeredActions = new Dictionary<string, Action<Request>>();
        private List<Message> _outBox;
        private List<Message> _iceBox;
        private int _pendingDelegateCalls;
        #if DEBUG
        private int _maxPendingDelegateCalls;
        #endif
        private Message _sendingMsg;
        private uint _numRequestsSent;
        private uint _numRequestsReceived;

        public event EventHandler OnConnect;

        public event EventHandler<Exception> OnError;

        public event EventHandler<Exception> OnClose;

        public event EventHandler<RequestWrapper> OnRequest;

        public event EventHandler<Response> OnResponse;

        public abstract Uri Url { get; protected internal set; }

        public abstract bool TransportCanSend { get; }

        public BLIPException Error { get; private set; }

        public bool Active { get; private set; }

        public bool DispatchPartialMessages { get; set; }

        protected internal Connection(bool isOpen)
        {
            _transportIsOpen = isOpen;
        }

        public abstract void Connect();

        public abstract void Close();

        public abstract void SendFrame(IEnumerable<byte> frame);

        public Request CreateRequest()
        {
            return new Request(this, null, null);
        }

        public Request CreateRequest(IList<byte> body, IDictionary<string, string> properties)
        {
            return new Request(this, body, properties);
        }

        public void RegisterAction(string profile, Action<Request> action)
        {
            _callbackScheduler.DispatchAsync(() =>
            {
                if (action != null)
                {
                    _registeredActions[profile] = action;
                }
                else {
                    _registeredActions.Remove(profile);
                }
            });
        }

        public void Send(Request request)
        {
            if (!request.IsMine || request.Sent)
            {
                // This was an incoming request that I'm being asked to forward or echo;
                // or it's an outgoing request being sent to multiple connections.
                // Since a particular Request can only be sent once, make a copy of it to send:
                request = (Request)request.Clone();
            }

            var itsConnection = request.Connection;
            if (itsConnection == null)
            {
                request.Connection = this;
            }
            else {
                throw new InvalidOperationException("Attempt to send a Request on a different connection " +
                "than the one it was assigned to");
            }

            request.Send();
        }

        protected void TransportOpened()
        {
            Logger.I(Security.Secure, $"{this} is open!");
            _transportIsOpen = true;
            if (_outBox != null && _outBox.Count > 0)
            {
                FeedTransport();
            }

            _callbackScheduler.DispatchAsync(() =>
            {
                if (OnConnect != null)
                {
                    OnConnect(this, null);
                }
            });
        }

        protected void TransportClosed(BLIPException e)
        {
            Logger.I(Security.Secure, $"{this} closed with error {e}");
            if (_transportIsOpen)
            {
                _transportIsOpen = false;
                _callbackScheduler.DispatchAsync(() =>
                {
                    if (OnClose != null)
                    {
                        OnClose(this, e);
                    }
                });
            }
            else {
                if (e != null && Error == null)
                {
                    Error = e;
                }

                _callbackScheduler.DispatchAsync(() =>
                {
                    if (OnError != null)
                    {
                        OnError(this, e);
                    }
                });
            }
        }

        protected void FeedTransport()
        {
            if (_outBox != null && _outBox.Count > 0 && _sendingMsg == null)
            {
                // Pop first message in queue:
                var msg = _outBox.First();
                _outBox.RemoveAt(0);
                _sendingMsg = msg;

                // As an optimization, allow message to send a big frame unless there's a higher-priority
                // message right behind it:
                var frameSize = DefaultFrameSize;
                if (msg.Urgent || _outBox.Count == 0 || !_outBox[0].Urgent)
                {
                    frameSize *= 4;
                }

                // Ask the message to generate its next frame. Do this on the delegate queue:
                bool moreComing = false;
                IEnumerable<byte> frame;
                _callbackScheduler.DispatchAsync(() =>
                {
                    frame = msg.NextFrame((ushort)frameSize, ref moreComing);
                    bool requeue = msg.NeedsAck;
                    Action<Message> onSent = moreComing ? null : msg.OnSent;
                    _transportScheduler.DispatchAsync(() =>
                    {
                        // SHAZAM! Send the frame to the transport:
                        if (frame != null)
                        {
                            SendFrame(frame);
                        }

                        _sendingMsg = null;
                        if (moreComing)
                        {
                            // add the message back so it can send its next frame later:
                            if (requeue)
                            {
                                QueueMessage(msg, false, false);
                            }
                            else {
                                PauseMessage(msg);
                            }
                        }
                        else {
                            if (onSent != null)
                            {
                                RunOnDelegateQueue(() => onSent(msg));
                            }
                        }

                        UpdateActive();
                    });
                });
            }
        }

        internal void CloseWithError(BLIPException e)
        {
            Error = e;
            Close();
        }

        internal void RunOnDelegateQueue(Action action)
        {
            _pendingDelegateCalls++;
            #if DEBUG
            if (_pendingDelegateCalls > _maxPendingDelegateCalls)
            {
                Logger.I(Security.Secure, $"New record: {_pendingDelegateCalls} pending delegate calls");
                _maxPendingDelegateCalls = _pendingDelegateCalls;
            }
            #endif
            _callbackScheduler.DispatchAsync(() =>
            {
                action();
                EndDelegateCall();
            });
        }

        internal void EndDelegateCall()
        {
            _transportScheduler.DispatchAsync(() =>
            {
                if (--_pendingDelegateCalls == 0)
                {
                    UpdateActive();
                }
            });
        }

        internal void UpdateActive()
        {
            var active = _outBox.SafeCount() > 0 || _iceBox.SafeCount() > 0 || _pendingRequests.SafeCount() > 0 ||
                         _pendingResponses.SafeCount() > 0 || _sendingMsg != null || _pendingDelegateCalls > 0;
            if (active != Active)
            {
                Logger.V(Security.Secure, $"{this} Active={active}");
                Active = active;
            }
        }

        internal void MessageReceivedProperties(Message message)
        {
            if (DispatchPartialMessages)
            {
                if (message.IsRequest)
                {
                    DispatchRequest((Request)message);
                }
                else {
                    DispatchResponse((Response)message);
                }
            }
        }

        internal void QueueMessage(Message message, bool isNew, bool sendNow)
        {
            if ((_outBox != null && _outBox.Contains(message)) || (_iceBox != null && _iceBox.Contains(message)))
            {
                throw new InvalidOperationException("Attempting to queue an already queued message");
            }

            if (message == _sendingMsg)
            {
                throw new InvalidOperationException("Attempting to queue an in-flight message");
            }

            var n = _outBox.SafeCount();
            var index = 0;
            if (message.Urgent && n > 1)
            {
                // High-priority gets queued after the last existing high-priority message,
                // leaving one regular-priority message in between if possible.
                for (index = n - 1; index > 0; index--)
                {
                    var otherMessage = _outBox[index];
                    if (otherMessage.Urgent)
                    {
                        index = Math.Min(index + 2, n);
                        break;
                    }
                    else if (isNew && otherMessage.BytesWritten == 0)
                    {
                        // But have to keep message starts in order
                        index = index + 1;
                        break;
                    }
                }

                if (index == 0)
                {
                    index = 1;
                }
            }
            else {
                // Regular priority goes at the end of the queue:
                index = n;
            }

            if (_outBox == null)
            {
                _outBox = new List<Message>();
            }

            _outBox.Insert(index, message);

            if (isNew)
            {
                Logger.I(Security.Secure, $"{this} queuing outgoing {message} at index {index}");
            }

            if (sendNow)
            {
                if (n == 0 && _transportIsOpen)
                {
                    _transportScheduler.DispatchAsync(() =>
                    {
                        FeedTransport();
                    });
                }
            }

            UpdateActive();
        }

        internal bool SendRequest(Request request, Response response)
        {
            if (request.Sent)
            {
                throw new InvalidOperationException("Cannot send Request twice");
            }

            bool result = false;
            _transportScheduler.DispatchSync(() =>
            {
                if (_transportIsOpen && !TransportCanSend)
                {
                    Logger.W(Security.Secure, 
                             $"{this}: Attempt to send a request after the connection has started closing: {request}");
                    result = false;
                    return;
                }

                request.AssignedNumber(++_numRequestsSent);
                if (response != null)
                {
                    response.AssignedNumber(_numRequestsSent);
                    _pendingResponses[response.Number] = response;
                    UpdateActive();
                }

                QueueMessage(request, true, true);
                result = true;
            });

            return result;
        }

        internal bool SendResponse(Response response)
        {
            if (response.Sent)
            {
                throw new InvalidOperationException("Cannot send Response twice");
            }

            _transportScheduler.DispatchAsync(() =>
            {
                QueueMessage(response, true, true);
            });

            return true;
        }

        internal void PauseMessage(Message message)
        {
            if (_outBox.Contains(message) || _iceBox.Contains(message))
            {
                throw new InvalidOperationException("Cannot pause an already queued message");
            }

            Logger.V(Security.Secure, $"{this} pausing {message}");

            if (_iceBox == null)
            {
                _iceBox = new List<Message>();
            }

            _iceBox.Add(message);
        }

        internal void UnpauseMessage(Message message)
        {
            if (_iceBox == null)
            {
                return;
            }

            var index = _iceBox.IndexOf(message);
            if (index != -1)
            {
                if (_outBox.Contains(message))
                {
                    throw new InvalidOperationException("Cannot unpause an already queued message");
                }

                Logger.V(Security.Secure, $"{this} resuming {message}");

                _iceBox.RemoveAt(index);
                if (message != _sendingMsg)
                {
                    QueueMessage(message, false, true);
                }
            }
        }

        internal Message GetOutgoingMessage(ulong number, bool isRequest)
        {
            foreach (var msg in _outBox)
            {
                if (msg.Number == number && msg.IsRequest == isRequest)
                {
                    return msg;
                }
            }

            foreach (var msg in _iceBox)
            {
                if (msg.Number == number && msg.IsRequest == isRequest)
                {
                    return msg;
                }
            }

            if (_sendingMsg.Number == number && _sendingMsg.IsRequest == isRequest)
            {
                return _sendingMsg;
            }

            return null;
        }

        internal void SendAck(ulong number, bool isRequest, ulong bytesReceived)
        {
            var ackType = isRequest ? "ACKMSG" : "ACKRPY";
            Logger.V(Security.Secure, $"{this} sending {ackType} of {number} ({bytesReceived} bytes)");
            var flags = (isRequest ? MessageFlags.AckMsg : MessageFlags.AckRpy) | MessageFlags.Urgent |
                        MessageFlags.NoReply;

            var ackFrame = default(byte[]);
            using (var ms = new MemoryStream())
            {
                var nextBytes = VarintBitConverter.GetVarintBytes(number);
                ms.Write(nextBytes, 0, nextBytes.Length);
                nextBytes = VarintBitConverter.GetVarintBytes((uint)flags);
                ms.Write(nextBytes, 0, nextBytes.Length);
                nextBytes = VarintBitConverter.GetVarintBytes(bytesReceived);
                ms.Write(nextBytes, 0, nextBytes.Length);

                ackFrame = ms.ToArray();
            }

            SendFrame(ackFrame);
        }

        protected void ReceivedFrame(Stream frame)
        {
            ulong messageNum;
            try
            {
                messageNum = VarintBitConverter.ToUInt64(frame);
                ulong flags = VarintBitConverter.ToUInt64(frame);
                if (flags <= (ulong)MessageFlags.MaxFlag)
                {
                    var body = new byte[frame.Length - frame.Position];
                    frame.Read(body, 0, body.Length);
                    ReceivedFrame(messageNum, (MessageFlags)flags, body);
                    return;
                }
            }
            catch (ArgumentException)
            {

            }

            CloseWithError(BLIPUtility.MakeException(BLIPError.BadFrame, "Bad varint encoding in frame flags"));
        }

        private void ReceivedFrame(ulong requestNum, MessageFlags flags, byte[] body)
        {
            var type = (MessageFlags)(flags & MessageFlags.TypeMask);
            Logger.V(Security.Secure, 
                     $"{this} rcvd frame of {TypeStrings[(int)type]} #{requestNum}, length {body.Length}");
            var key = requestNum;
            var complete = !flags.HasFlag(MessageFlags.MoreComing);
            switch (type)
            {
                case MessageFlags.Msg:
                    // Incoming request:
                    var request = _pendingRequests.ContainsKey(key) ? _pendingRequests[key] : null;
                    if (request != null)
                    {
                        // Continuation frame of a request:
                        if (complete)
                        {
                            _pendingRequests.Remove(key);
                        }
                    }
                    else if (requestNum == _numRequestsReceived + 1)
                    {
                        // Next new request:
                        request = new Request(this, false, flags | MessageFlags.MoreComing, requestNum, null);
                        if (!complete)
                        {
                            _pendingRequests[key] = request;
                        }

                        ++_numRequestsReceived;
                    }
                    else {
                        CloseWithError(BLIPUtility.MakeException(BLIPError.BadFrame, "Received bad request frame #{0}, " +
                        "(next is #{1})", requestNum, _numRequestsReceived + 1));
                        return;
                    }

                    ReceivedFrame(flags, body, complete, request);
                    break;
                case MessageFlags.Rpy:
                case MessageFlags.Err:
                    var response = _pendingResponses.ContainsKey(key) ? _pendingResponses[key] : null;
                    if (response != null)
                    {
                        if (complete)
                        {
                            _pendingResponses.Remove(key);
                        }

                        ReceivedFrame(flags, body, complete, response);
                    }
                    else {
                        if (requestNum <= _numRequestsSent)
                        {
                            Logger.I(Security.Secure, 
                                     $"??? {this} got unexpected response frame to my msg #{requestNum}");
                        }
                        else {
                            CloseWithError(BLIPUtility.MakeException(BLIPError.BadFrame, "Bogus message number {0} in response",
                                requestNum));
                            return;
                        }
                    }
                    break;
                case MessageFlags.AckMsg:
                case MessageFlags.AckRpy:
                    var msg = GetOutgoingMessage(requestNum, (type == MessageFlags.AckMsg));
                    if (msg == null)
                    {
                        Logger.I(Security.Secure, 
                                 $"??? {this} Received ACK for non-current message ({TypeStrings[(int)type]} {requestNum})");
                        break;
                    }

                    ulong bytesReceived;
                    try
                    {
                        bytesReceived = VarintBitConverter.ToUInt64(body);
                    }
                    catch (ArgumentException)
                    {
                        CloseWithError(BLIPUtility.MakeException(BLIPError.BadFrame, "Bad ACK body"));
                        return;
                    }

                    RunOnDelegateQueue(() =>
                    {
                        var ok = msg.ReceivedAck(bytesReceived);
                        if (ok)
                        {
                            UnpauseMessage(msg);
                        }
                        else {
                            CloseWithError(BLIPUtility.MakeException(BLIPError.BadFrame, "Bad ACK count"));
                        }
                    });
                    break;
                default:
                    // To leave room for future expansion, undefined message types are just ignored.
                    Logger.I(Security.Secure, $"??? {this} received header with unknown message type {(int)type}");
                    break;
            }

            UpdateActive();
        }

        private void ReceivedFrame(MessageFlags flags, byte[] body, bool complete, Message message)
        {
            RunOnDelegateQueue(() =>
            {
                var ok = message.ReceivedFrame(flags, body);
                if (!ok)
                {
                    _transportScheduler.DispatchAsync(() =>
                    {
                        CloseWithError(BLIPUtility.MakeException(BLIPError.BadFrame, "Couldn't parse message frame"));
                    });
                }
                else if (DispatchPartialMessages)
                {
                    if (message.IsRequest)
                    {
                        DispatchRequest((Request)message);
                    }
                    else {
                        DispatchResponse((Response)message);
                    }
                }
            });
        }

        private void DispatchRequest(Request request)
        {
            try
            {
                Logger.I(Security.Secure, "Dispatching: ");
                Logger.I(Security.PotentiallyInsecure, request.ToVerboseString());
                bool handled;
                if (request.Flags.HasFlag(MessageFlags.Meta))
                {
                    handled = DispatchMetaRequest(request);
                }
                else {
                    handled = SendRegisteredAction(request);
                    if (!handled && OnRequest != null)
                    {
                        var wrapper = new RequestWrapper(request);
                        OnRequest(this, wrapper);
                        handled = wrapper.Handled;
                    }
                }

                if (request.Complete)
                {
                    if (!handled)
                    {
                        Logger.I(Security.Secure, $"No handler found for incoming {request}");
                        request.Respond(BLIPError.NotFound, "No handler was found");
                    }
                    else if(!request.NoReply && !request.RepliedTo) {
                        Logger.I(Security.Secure, $"Returning default empty response to {request}");
                        request.Respond(null, null);
                    }
                }
            }
            catch (Exception e)
            {
                request.Respond(e);
            }
        }

        private void DispatchResponse(Response response)
        {
            Logger.I(Security.Secure, $"Dispatching {response}");
            OnResponse?.Invoke(this, response);
        }

        private bool DispatchMetaRequest(Request request)
        {
            //TODO
            return false;
        }

        private bool SendRegisteredAction(Request request)
        {
            var profile = request.Profile;
            if (profile != null)
            {
                var action = _registeredActions.ContainsKey(profile) ? _registeredActions[profile] : null;
                if (action != null)
                {
                    action(request);
                    return true;
                }
            }

            return false;
        }
    }
}

