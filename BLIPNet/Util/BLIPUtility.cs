//
// BLIPUtility.cs
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
using System.Collections;

namespace BLIP.Util
{
    public static class BLIPUtility
    {
        public static BLIPException MakeException(BLIPError errorCode, string errorFormat, params object[] args)
        {
            var message = String.Format(errorFormat, args);
            return new BLIPException(errorCode, message);
        }

        public static BLIPException MakeException(Exception inner)
        {
            return new BLIPException(inner);
        }

        internal static int SafeCount(this IList list)
        {
            return list == null ? 0 : list.Count;
        }

        internal static int SafeCount(this IDictionary list)
        {
            return list == null ? 0 : list.Count;
        }
    }
}

