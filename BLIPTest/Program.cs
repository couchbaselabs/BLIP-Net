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
using BLIP;
using BLIP.Util;
using BLIPConsoleTest;
using Gtk;
using Newtonsoft.Json;

namespace BLIPTest
{
    class MainClass
    {
        private static TextBuffer _requestProfile;
        private static TextBuffer _requestProperties;
        private static TextBuffer _requestBody;
        private static TextBuffer _responseProperties;
        private static TextBuffer _responseBody;

        private static List<Request> _requests = new List<Request>();
        private static List<Response> _responses = new List<Response>();

        private static BLIPWebSocketConnection _connection;
        private static ListStore _listViewSource;

        private static MainWindow _window;
        private static LoggerWindow _logWindow = new LoggerWindow();

        public static void Main(string[] args)
        {
            Application.Init();
            _window = SetupUI();
            _window.ShowAll();
            _logWindow.Realize();
            _logWindow.SetSizeRequest(640, 480);
            Logger.Output += (level, sec, msg) =>
            {
                _logWindow.Append($"[{level}] {msg}");
            };
            Application.Run();
        }

        private static MainWindow SetupUI()
        {
            MainWindow win = new MainWindow();
            win.SetSizeRequest(800, 600);

            var masterBox = new VBox(false, 0);
            var align = new Alignment(0.0f, 0.0f, 1.0f, 0.0f);
            align.Add(SetupMenu(win));
            masterBox.Add(align);

            var hBox = new HBox(false, 10);

            win.Title = "BLIP Test App";
            align = new Alignment(0.5f, 0.5f, 0.5f, 0.5f);
            var leftBox = new VBox(false, 10) { WidthRequest = 300 };

            _requestProfile = new TextBuffer(null);
            leftBox.Add(new Label("Profile"));
            leftBox.Add(new TextView(_requestProfile));

            _requestProperties = new TextBuffer(null);
            leftBox.Add(new Label("Properties"));
            leftBox.Add(new TextView(_requestProperties) { HeightRequest = 200 });

            _requestBody = new TextBuffer(null);
            leftBox.Add(new Label("Body"));
            leftBox.Add(new TextView(_requestBody) { HeightRequest = 200 });

            var sendButton = new Button(new Label("Send"));
            sendButton.Clicked += SendButton_Clicked;
            leftBox.Add(sendButton);

            hBox.Add(leftBox);

            var middleBox = new VBox(false, 10) { WidthRequest = 300 };
            _responseProperties = new TextBuffer(null);
            middleBox.Add(new Label("Properties"));
            middleBox.Add(new TextView(_responseProperties) { HeightRequest = 200, Editable = false });

            _responseBody = new TextBuffer(null);
            middleBox.Add(new Label("Body"));
            middleBox.Add(new TextView(_responseBody) { HeightRequest = 200, Editable = false });

            hBox.Add(middleBox);

            _listViewSource = new ListStore(typeof(string));
            var rightBox = new TreeView(_listViewSource) { WidthRequest = 75 };
            var historyColumn = new TreeViewColumn { Title = "History" };
            var historyRenderer = new CellRendererText();
            historyColumn.PackStart(historyRenderer, true);
            historyColumn.AddAttribute(historyRenderer, "text", 0);
            rightBox.AppendColumn(historyColumn);
            rightBox.CursorChanged += (sender, args) =>
            {
                var s = (sender as TreeView).Selection;
                var selectedItem = default(TreeIter);
                if (s.GetSelected(out selectedItem))
                {
                    var text = _listViewSource.GetValue(selectedItem, 0) as string;
                    var index = Int32.Parse(text.Substring(1)) - 1;
                    var request = _requests[index];
                    var response = _responses[index];
                    _requestProfile.Text = request.Profile;
                    _requestBody.Text = request.BodyString;
                    var requestProps = request.Properties;
                    requestProps.Remove("Profile");
                    _requestProperties.Text = JsonConvert.SerializeObject(requestProps);
                    _responseBody.Text = response.BodyString;
                    _responseProperties.Text = JsonConvert.SerializeObject(response.Properties);
                }
            };

            hBox.Add(rightBox);
            align.Add(hBox);
            masterBox.Add(align);

            win.Add(masterBox);
            return win;
        }

        private static MenuBar SetupMenu(Window win)
        {
            var mb = new MenuBar();
            var fileMenu = new Menu();
            var file = new MenuItem("File");
            file.Submenu = fileMenu;

            var agr = new AccelGroup();
            win.AddAccelGroup(agr);

            var connect = new MenuItem("Connect...");
            connect.AddAccelerator("activate", agr, new AccelKey(Gdk.Key.O, Gdk.ModifierType.ControlMask, AccelFlags.Visible));
            connect.Activated += Connect_Activated;
            fileMenu.Append(connect);

            var log = new MenuItem("Show Log...");
            log.AddAccelerator("activate", agr, new AccelKey(Gdk.Key.L, Gdk.ModifierType.ControlMask, AccelFlags.Visible));
            log.Activated += Log_Activated;
            fileMenu.Append(log);

            var sep = new SeparatorMenuItem();
            fileMenu.Append(sep);

            var quit = new ImageMenuItem(Stock.Quit, agr);
            quit.Activated += (sender, e) => Application.Quit();
            quit.AddAccelerator("activate", agr, new AccelKey(Gdk.Key.Q, Gdk.ModifierType.ControlMask, AccelFlags.Visible));
            fileMenu.Append(quit);

            mb.Append(file);
            return mb;
        }

        private static void Connect_Activated(object sender, EventArgs e)
        {
            var dialog = new Dialog("BLIP Connection Setting", _window,
                            DialogFlags.Modal | DialogFlags.DestroyWithParent,
                            Stock.Ok, ResponseType.Ok,
                            Stock.Cancel, ResponseType.Cancel);
            dialog.VBox.Add(new Label("Connection URL"));
            var buffer = new TextBuffer(null);
            dialog.VBox.Add(new TextView(buffer));
            dialog.ShowAll();
            var choice = (ResponseType)dialog.Run();
            dialog.Destroy();

            if (choice == ResponseType.Ok)
            {
                if (_connection != null)
                {
                    _connection.Close(1000, "User requested close");
                }

                Uri newUri;
                if (!Uri.TryCreate(buffer.Text.Replace("http", "ws"), UriKind.Absolute, out newUri))
                {
                    MessageDialog md = new MessageDialog(_window,
                    DialogFlags.DestroyWithParent, MessageType.Error,
                    ButtonsType.Close, "Invalid URL...");
                    md.Run();
                    md.Destroy();
                    return;
                }

                _connection = new BLIPWebSocketConnection(newUri);
                _connection.Connect();
            }
        }

        private static void SendButton_Clicked(object sender, EventArgs e)
        {
            if (_connection == null)
            {
                MessageDialog md = new MessageDialog(_window,
                DialogFlags.DestroyWithParent, MessageType.Error,
                ButtonsType.Close, "No connection established...");
                md.Run();
                md.Destroy();
                return;
            }

            var request = _connection.CreateRequest(Encoding.UTF8.GetBytes(_requestBody.Text), JsonConvert.DeserializeObject<IDictionary<string, string>>(_requestProperties.Text));
            request.Profile = _requestProfile.Text;
            _requests.Add(request);
            request.Send().OnComplete += response =>
            {
                _responseProperties.Text = JsonConvert.SerializeObject(response.Properties);
                _responseBody.Text = response.BodyString;
                _responses.Add(response);
            };
            _listViewSource.AppendValues($"#{request.Number}");
        }

        static void Log_Activated(object sender, EventArgs e)
        {
            _logWindow.ShowAll();
        }
    }
}
