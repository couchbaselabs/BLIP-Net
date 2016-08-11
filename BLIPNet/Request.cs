//
// Request.cs
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
using System.Collections.Generic;
using System.Text;
using BLIP.Util;

namespace BLIP
{
    public sealed class Request : Message
    {
        private static readonly string Tag = typeof(Request).Name;
        private Response _response;

        private Response Response
        {
            get
            {
                if (_response == null && !NoReply)
                {
                    _response = new Response(this);
                }

                return _response;
            }
        }

        internal bool NoReply
        {
            get
            {
                return Flags.HasFlag(MessageFlags.NoReply);
            }
            set
            {
                ToggleFlags(MessageFlags.NoReply, value);
            }
        }

        internal bool RepliedTo
        {
            get
            {
                return _response != null;
            }
        }

        internal Connection Connection
        {
            get { return _connection; }
            set
            {
                System.Diagnostics.Debug.Assert(IsMine && !Sent, "Connection can only be set before sending");
                _connection = value;
            }
        }

        public Request(IList<byte> body)
            : this(null, body, null)
        {

        }

        public Request(string bodyString)
            : this(Encoding.UTF8.GetBytes(bodyString))
        {

        }

        public Request(IList<byte> body, IDictionary<string, string> properties)
            : this(null, body, properties)
        {

        }

        internal Request(Connection connection, IList<byte> body, IDictionary<string, string> properties)
            : base(connection, true, MessageFlags.Msg, 0, body)
        {
            if (body != null)
            {
                Body = body;
            }

            if (properties != null)
            {
                Properties = properties;
            }
        }

        internal Request(Connection connection, bool isMine, MessageFlags flags, ulong msgNo, IList<byte> body)
            : base(connection, isMine, flags, msgNo, body)
        {

        }

        public Response Send()
        {
            System.Diagnostics.Debug.Assert(_connection != null, "No connection to send over");
            System.Diagnostics.Debug.Assert(!Sent, "Message was already sent");
            Encode();
            var response = Response;
            if (_connection.SendRequest(this, response))
            {
                Sent = true;
            }
            else {
                response = null;
            }

            return response;
        }

        internal void DeferResponse()
        {
            // This will allocate _response, causing -repliedTo to become YES, so Connection won't
            // send an automatic empty response after the current request handler returns.
            Logger.I(Security.Secure, $"Deferring response to {this}");
            if (_response == null && !NoReply)
            {
                _response = new Response(this);
            }
        }

        internal void Respond(IList<byte> data, string contentType)
        {
            var response = Response;
            response.Body = data;
            response.ContentType = contentType;
            response.Send();
        }

        internal void Respond(string str)
        {
            Respond(Encoding.UTF8.GetBytes(str), "text/plain; charset=UTF-8");
        }

        internal void Respond(object jsonObject)
        {
            var response = Response;
            response.BodyJSON = jsonObject;
            response.Send();
        }

        internal void Respond(BLIPException e)
        {
            Response.Error = e;
            Response.Send();
        }

        internal void Respond(BLIPError errorCode, string errorMessage)
        {
            Respond(BLIPUtility.MakeException(errorCode, errorMessage));
        }

        internal void Respond(Exception e)
        {
            Respond(BLIPUtility.MakeException(e));
        }

        #region ICloneable

        public object Clone()
        {
            System.Diagnostics.Debug.Assert(Complete);
            var copy = new Request(Body, Properties);
            copy.Compressed = Compressed;
            copy.Urgent = Urgent;
            copy.NoReply = NoReply;
            return copy;
        }

        #endregion
    }
}

