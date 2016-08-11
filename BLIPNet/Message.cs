//
// Message.cs
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
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using BLIP.Util;
using Newtonsoft.Json;
using System.IO.Compression;

namespace BLIP
{
    public enum BLIPError
    {
        BadData = 1,
        BadFrame,
        Disconnected,
        PeerNotAllowed,

        Misc = 99,

        // errors returned in responses:
        BadRequest = 400,
        Forbidden = 403,
        NotFound = 404,
        BadRange = 416,

        HandlerFailed = 501,
        Unspecified = 599       // peer didn't send any detailed info
    }

    public sealed class DataReceivedEventArgs : EventArgs
    {
        public Stream Stream { get; }

        internal DataReceivedEventArgs(Stream stream)
        {
            Stream = stream;
        }
    }

    public sealed class DataSentEventArgs : EventArgs
    {
        public ulong BytesWritten { get; }

        internal DataSentEventArgs(ulong bytesWritten)
        {
            BytesWritten = bytesWritten;
        }
    }

    public abstract class Message
    {
        private static readonly string Tag = typeof(Message).Name;
        private const int MaxUnackedBytes = 128000;
        private const int AckByteInterval = 50000;

        private readonly bool _isMine;
        private List<byte> _body;
        private List<Stream> _bodyStreams = new List<Stream>();
        private ulong _bytesReceived;

        protected Connection _connection;
        protected MemoryStream _encodedBody;
        protected Stream _outgoing;
        protected Stream _incoming;

        public Action<Message, Stream> OnDataReceived { get; set; }

        public Action<Message, ulong> OnDataSent { get; set; }

        public Action<Message> OnSent { get; set; }

        public ulong Number { get; private set; }

        public bool IsMine
        {
            get
            {
                return _isMine;
            }
        }

        public bool IsRequest
        {
            get
            {
                return (Flags & MessageFlags.TypeMask) == MessageFlags.Msg;
            }
        }

        public bool Sent { get; protected set; }

        public bool PropertiesAvailable { get; private set; }

        internal MessageFlags Flags { get; private set; }

        public virtual bool Complete { get; internal set; }

        public bool Compressed
        {
            get
            {
                return Flags.HasFlag(MessageFlags.Compressed);
            }
            set
            {
                ToggleFlags(MessageFlags.Compressed, value);
            }
        }

        public bool Urgent
        {
            get
            {
                return Flags.HasFlag(MessageFlags.Urgent);
            }
            set
            {
                ToggleFlags(MessageFlags.Urgent, value);
            }
        }

        public bool CanWrite { get; protected set; }

        public IList<byte> Body
        {
            get
            {
                return _body.ToArray();
            }
            set
            {
                if (!IsMine || !CanWrite)
                {
                    Logger.E(LogDomain.BLIP, Security.Secure, "Attempt to write to a readonly BLIPMessage, throwing...");
                    throw new InvalidOperationException("Attempt to write to a readonly BLIPMessage");
                }

                _body = new List<byte>(value);
            }
        }

        public string BodyString
        {
            get
            {
                var body = Body;
                if (body != null)
                {
                    return Encoding.UTF8.GetString(body.ToArray(), 0, body.Count);
                }

                return null;
            }
            set
            {
                Body = Encoding.UTF8.GetBytes(value).ToList();
                ContentType = "text/plain; charset=UTF-8";
            }
        }

        public object BodyJSON
        {
            get
            {
                var retVal = default(object);
                try
                {
                    using (var ms = new MemoryStream(Body.ToArray()))
                    using (var sr = new StreamReader(ms))
                    {
                        retVal = JsonSerializer.Create().Deserialize(sr, typeof(object));
                    }
                }
                catch (Exception e)
                {
                    return null;
                }

                return retVal;
            }
            set
            {
                var bytes = default(IEnumerable<byte>);
                try
                {
                    bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
                }
                catch (Exception e)
                {
                    throw new ArgumentException("Invalid BodyJSON object", "value");
                }

                Body = bytes.ToList();
            }
        }

        public object Context { get; set; }

        public IDictionary<string, string> Properties { get; set; }

        public string ContentType
        {
            get
            {
                return this["Content-Type"];
            }
            set
            {
                this["Content-Type"] = value;
            }
        }

        public string Profile
        {
            get
            {
                return this["Profile"];
            }
            set
            {
                this["Profile"] = value;
            }
        }

        public string this[string key]
        {
            get
            {
                if (Properties == null)
                {
                    return null;
                }

                return Properties.ContainsKey(key) ? Properties[key] : null;
            }
            set
            {
                if(Properties == null) {
                    return;
                }

                Properties[key] = value;
            }
        }

        internal bool NeedsAck
        {
            get
            {
                System.Diagnostics.Debug.Assert(IsMine);
                return BytesWritten - _bytesReceived >= MaxUnackedBytes;
            }
        }

        internal ulong BytesWritten { get; private set; }

        internal Message(Connection connection, bool isMine, MessageFlags flags, ulong msgNo,
            IList<byte> body)
        {
            _connection = connection;
            _isMine = isMine;
            CanWrite = isMine;
            Flags = flags;
            Number = msgNo;
            if (isMine)
            {
                if (body != null)
                {
                    Body = body;
                }

                Properties = new Dictionary<string, string>();
                PropertiesAvailable = true;
                Complete = true;
            }
            else if (body != null)
            {
                throw new InvalidOperationException("Cannot construct a BLIPMessage with a body that isn't mine");
            }
        }

        public override string ToString()
        {
            //TODO: Length / compression
            var sb = new StringBuilder();
            sb.AppendFormat("{0}[#{1}{2}", GetType().Name, Number, IsMine ? "->" : "<-");
            if (Flags.HasFlag(MessageFlags.Urgent))
            {
                sb.Append(", urgent");
            }

            if (Flags.HasFlag(MessageFlags.NoReply))
            {
                sb.Append(", noreply");
            }

            if (Flags.HasFlag(MessageFlags.Meta))
            {
                sb.Append(", META");
            }

            if (Flags.HasFlag(MessageFlags.MoreComing))
            {
                sb.Append(", incomplete");
            }

            sb.Append("]");
            return sb.ToString();
        }

        internal void ToggleFlags(MessageFlags flags, bool on)
        {
            if (!IsMine || !CanWrite)
            {
                throw new InvalidOperationException("Attempt to write to a readonly BLIPMessage");
            }

            if (on)
            {
                Flags |= flags;
            }
            else {
                Flags &= ~flags;
            }
        }

        internal void Encode()
        {
            if (!IsMine || !CanWrite)
            {
                throw new InvalidOperationException("Attempt to write to a readonly BLIPMessage");
            }

            CanWrite = false;
            _encodedBody = new MemoryStream();
            if (_body != null)
            {
                _encodedBody.Write(_body.ToArray(), 0, _body.Count);
            }

            foreach (var stream in _bodyStreams)
            {
                stream.CopyTo(_encodedBody);
            }

            if (Compressed)
            {
                _outgoing = new GZipStream(_encodedBody, CompressionMode.Compress, false);
            }
            else {
                _outgoing = _encodedBody;
                _outgoing.Seek(0, SeekOrigin.Begin);
            }
        }

        internal void AssignedNumber(uint number)
        {
            if (Number != 0)
            {
                throw new InvalidOperationException("Attempt to set number twice on a BLIPMessage object");
            }

            Number = number;
            CanWrite = false;
        }

        internal IEnumerable<byte> NextFrame(ushort maxSize, ref bool moreComing)
        {
            if (Number == 0)
            {
                throw new InvalidOperationException("Invalid state for generating frames");
            }

            if (!IsMine)
            {
                throw new InvalidOperationException("Invalid state for generating frames");
            }

            if (_outgoing == null)
            {
                throw new InvalidOperationException("Invalid state for generating frames");
            }

            moreComing = false;
            if (BytesWritten == 0)
            {
                //LOG
            }

            var frame = new List<byte>(maxSize);
            var prevBytesWritten = BytesWritten;
            if (BytesWritten == 0)
            {
                // First frame: always write entire properties
                var propertyData = BLIPProperties.Encode(Properties);
                frame.AddRange(propertyData);
                BytesWritten += (ulong)propertyData.Count();
            }

            // Now read from the payload:
            var frameLen = frame.Count;
            if (frameLen < maxSize)
            {
                var buffer = new byte[maxSize - frameLen];
                var bytesRead = 0;
                try
                {
                    bytesRead = _outgoing.Read(buffer, 0, buffer.Length);
                    frame.AddRange(buffer.Take(bytesRead));
                }
                catch (IOException e)
                {
                    if (OnDataSent != null)
                    {
                        OnDataSent(this, 0);
                    }

                    Complete = true;
                    return null;
                }

                BytesWritten += (ulong)bytesRead;
            }

            // Write the header at the start of the frame:
            if (_outgoing.Position == _outgoing.Length)
            {
                Flags &= ~MessageFlags.MoreComing;
                _outgoing.Dispose();
                _outgoing = null;
            }
            else {
                Flags |= MessageFlags.MoreComing;
                moreComing = true;
            }

            frame.InsertRange(0, VarintBitConverter.GetVarintBytes((uint)Flags));
            frame.InsertRange(0, VarintBitConverter.GetVarintBytes(Number));


            if (OnDataSent != null)
            {
                OnDataSent(this, BytesWritten);
            }

            if (!moreComing)
            {
                Complete = true;
            }

            return frame;
        }

        internal bool ReceivedAck(ulong bytesReceived)
        {
            System.Diagnostics.Debug.Assert(IsMine);
            if (bytesReceived <= _bytesReceived || bytesReceived > BytesWritten)
            {
                return false;
            }

            _bytesReceived = bytesReceived;
            return true;
        }

        // Parses the next incoming frame.
        internal bool ReceivedFrame(MessageFlags flags, IEnumerable<byte> body)
        {
            var realized = body.ToArray();
            System.Diagnostics.Debug.Assert(!IsMine);
            System.Diagnostics.Debug.Assert(flags.HasFlag(MessageFlags.MoreComing));

            if (!IsRequest)
            {
                Flags = Flags | MessageFlags.MoreComing;
            }

            var oldBytesReceived = _bytesReceived;
            _bytesReceived += (ulong)realized.Length;
            var shouldAck = flags.HasFlag(MessageFlags.MoreComing) && oldBytesReceived > 0 &&
                            (oldBytesReceived / AckByteInterval) < (_bytesReceived / AckByteInterval);

            if (_incoming == null)
            {
                _incoming = _encodedBody = new MemoryStream();
            }

            try
            {
                _incoming.Write(realized, 0, realized.Length);
                _incoming.Seek(0, SeekOrigin.Begin);
            }
            catch (IOException e)
            {
                return false;
            }

            if (Properties == null)
            {
                // Try to extract the properties:
                bool complete = false;
                Properties = BLIPProperties.Read(_encodedBody, ref complete);
                if (Properties != null)
                {
                    if (flags.HasFlag(MessageFlags.Compressed))
                    {
                        // Now that properties are read, enable decompression for the rest of the stream:
                        ToggleFlags(MessageFlags.Compressed, true);
                        var restOfFrame = new byte[_encodedBody.Length - _encodedBody.Position];
                        _encodedBody.Read(restOfFrame, 0, restOfFrame.Length);
                        _encodedBody = new MemoryStream();
                        _incoming = new GZipStream(_encodedBody, CompressionMode.Decompress, false);
                        if (restOfFrame.Length > 0)
                        {
                            try
                            {
                                _incoming.Write(restOfFrame, 0, restOfFrame.Length);
                            }
                            catch (IOException e)
                            {
                                return false;
                            }
                        }

                        PropertiesAvailable = true;
                        _connection.MessageReceivedProperties(this);
                    }
                }
                else if (complete)
                {
                    return false;
                }
            }

            if (Properties != null && OnDataReceived != null)
            {
                _encodedBody.Seek(0, SeekOrigin.Begin);
                OnDataReceived(this, _encodedBody);
            }

            if (!flags.HasFlag(MessageFlags.MoreComing))
            {
                Flags &= ~MessageFlags.MoreComing;
                if (Properties == null)
                {
                    return false;
                }

                var b = new byte[_encodedBody.Length - _encodedBody.Position];
                _encodedBody.Read(b, 0, b.Length);
                _body = b.ToList();
                _encodedBody.Dispose();
                _encodedBody = null;
                _incoming.Dispose();
                _incoming = null;
                OnDataReceived = null;
                Complete = true;
            }

            if (shouldAck)
            {
                _connection.SendAck(Number, IsRequest, _bytesReceived);
            }

            return true;
        }

        internal virtual void ConnectionClosed()
        {
            if (IsMine)
            {
                BytesWritten = 0;
                ToggleFlags(MessageFlags.MoreComing, true);
            }
        }
    }
}

