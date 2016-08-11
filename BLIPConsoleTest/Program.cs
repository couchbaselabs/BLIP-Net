//
// Program.cs
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
using System.Collections.Generic;
using System.Text;
using System.Threading;
using BLIP;
using Newtonsoft.Json;

namespace BLIPConsoleTest
{
    class MainClass
    {
        public static void Main(string[] args)
        {
            Console.Write("Connection URL: ");
            var url = Console.ReadLine();
            var connection = new BLIPWebSocketConnection(new Uri(url.Replace("http","ws")));
            connection.Connect();
            while (true)
            {
                Console.Write("Profile: ");
                var profile = Console.ReadLine();
                Console.Write("Properties: ");
                var props = Console.ReadLine();
                Console.Write("Body: ");
                var body = Console.ReadLine();

                var request = connection.CreateRequest(Encoding.UTF8.GetBytes(body), JsonConvert.DeserializeObject<IDictionary<string, string>>(props));
                request.Profile = profile;
                var are = new AutoResetEvent(false);
                request.Send().OnComplete += response =>
                {
                    Console.WriteLine($"Got response #{response.Number}");
                    Console.WriteLine($"Properties: {JsonConvert.SerializeObject(response.Properties)}");
                    Console.WriteLine($"Body: {response.BodyString}");
                    are.Set();
                };

                Console.WriteLine($"Sending request #{request.Number}...");
                are.WaitOne();
            }
        }
    }
}
