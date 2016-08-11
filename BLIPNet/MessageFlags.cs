//
// MessageFlags.cs
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
    [Flags]
    internal enum MessageFlags : byte
    {
        Msg = 0x00,
        Rpy = 0x01,
        Err = 0x02,
        AckMsg = 0x04,
        AckRpy = 0x05,
        TypeMask = 0x07,
        Compressed = 0x08,
        Urgent = 0x10,
        NoReply = 0x20,
        MoreComing = 0x40,
        Meta = 0x80,
        MaxFlag = 0xFF
    }
}

