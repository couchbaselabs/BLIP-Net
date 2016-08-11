//
// IConnectionDelegate.cs
//
// Author:
// 	Jim Borden  <jim.borden@couchbase.com>
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
namespace BLIP
{
    public interface IConnectionDelegate
    {
        void ConnectionOpened(Connection conection);

        void ConnectionFailed(Connection connection, Exception e);

        void ConnectionClosed(Connection connection, Exception e);

        bool ConnectionReceivedRequest(Connection connection, Request request);

        void ConnectionReceivedResponse(Connection connection, Response response);
    }

    public class ConnectionDelegate : IConnectionDelegate
    {
        public virtual void ConnectionClosed(Connection connection, Exception e)
        {
            
        }

        public virtual void ConnectionFailed(Connection connection, Exception e)
        {
            
        }

        public virtual void ConnectionOpened(Connection conection)
        {
            
        }

        public virtual bool ConnectionReceivedRequest(Connection connection, Request request)
        {
            return true;
        }

        public virtual void ConnectionReceivedResponse(Connection connection, Response response)
        {
            
        }
    }
}

