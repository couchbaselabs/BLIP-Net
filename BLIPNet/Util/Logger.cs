//
// Logger.cs
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
using System.Diagnostics;

namespace BLIP.Util
{
    public enum LogDomain
    {
        BLIP,
        BLIPLifecycle
    }

    public enum LogLevel
    {
        Error,
        Warning,
        Info,
        Verbose,
        Debug
    }

    public enum Security
    {
        Secure,
        PotentiallyInsecure,
        Insecure
    }

    public static class Logger
    {
        public static Action<LogLevel, LogDomain, Security, string> Output { get; set; }

        public static LogLevel Level { get; set; }

        static Logger()
        {
            Level = LogLevel.Info;
        }

        internal static void D(LogDomain domain, Security security, string msg)
        {
            #if DEBUG
            Write(LogLevel.Debug, domain, security, msg);
            #endif
        }

        internal static void V(LogDomain domain, Security security, string msg)
        {
            Write(LogLevel.Verbose, domain, security, msg);
        }

        internal static void I(LogDomain domain, Security security, string msg)
        {
            Write(LogLevel.Info, domain, security, msg);
        }

        internal static void W(LogDomain domain, Security security, string msg)
        {
            Write(LogLevel.Warning, domain, security, msg);
        }

        internal static void E(LogDomain domain, Security security, string msg)
        {
            Write(LogLevel.Error, domain, security, msg);
        }

        private static void Write(LogLevel level, LogDomain domain, Security security, string msg)
        {
            if (Level >= level)
            {
                Output?.Invoke(level, domain, security, msg);
            }
        }
    }
}

