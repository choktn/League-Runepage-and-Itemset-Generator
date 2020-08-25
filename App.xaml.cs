﻿using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HtmlAgilityPack;
using Newtonsoft.Json;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace LoL_Generator
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private TaskbarIcon notifyIcon;
        static MainWindow window;

        static string lockfileloc;

        static string port;
        static string password;
        static byte[] encoding;

        static long summonerId;

        static bool listedSets;
        static string champion;
        static bool champLocked;

        static int championHoverId;
        static string currole;

        static HttpClient httpClient;
        static HttpClientHandler handler;

        static CancellationTokenSource tokenSource;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            //initiate and show the window using code so as to keep one instance of a window
            window = new MainWindow();
            window.Show();

            //create the tray icon
            notifyIcon = (TaskbarIcon)FindResource("MyNotifyIcon");

            //set the default rune page and item sets and assign the generate loudout button to a function
            window.DefaultRunePage.Tag = LoL_Generator.Properties.Settings.Default.runePageID;
            window.DefaultItemPage.Tag = LoL_Generator.Properties.Settings.Default.itemSetID;
            window.LoadoutButton.Click += new RoutedEventHandler(GenerateLoadout);

            Console.WriteLine(LoL_Generator.Properties.Settings.Default.runePageID);
            Console.WriteLine(LoL_Generator.Properties.Settings.Default.itemSetID);

            //start the background task to poll for the league client
            tokenSource = new CancellationTokenSource();
            StartNewTask(InitiatePolling, TimeSpan.FromSeconds(3), tokenSource.Token);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            notifyIcon.Dispose(); //clear the tray icon when the program is exitted
            base.OnExit(e);
        }

        //checks for the league client
        bool CheckClientIsOpen()
        {
            //check if league client is not open
            if (Process.GetProcessesByName("LeagueClient").FirstOrDefault() == null)
            {
                //cancel the current task
                tokenSource.Cancel();

                //display the waiting overlay
                Action act = () =>
                {
                    if (window.ChampionOverlay.Visibility == Visibility.Visible)
                    {
                        window.ChampionOverlay.Visibility = Visibility.Collapsed;
                        window.WaitingOverlay.Visibility = Visibility.Visible;
                    }
                };

                //reset values
                listedSets = false;
                champion = default;
                champLocked = false;

                //start the background task to poll for the league client
                tokenSource = new CancellationTokenSource();
                StartNewTask(InitiatePolling, TimeSpan.FromSeconds(3), tokenSource.Token);

                return false;
            }

            return true;
        }

        //first part of the polling process, gets the client pathway
        void InitiatePolling()
        {
            //check if league client is open
            if (Process.GetProcessesByName("LeagueClient").FirstOrDefault() != null)
            {
                Console.WriteLine("LeagueClient.exe has been opened");
                //cancel the current task
                tokenSource.Cancel();

                //get the location of the league client
                string clientpath = System.IO.Path.GetDirectoryName(GetProcessFilename(Process.GetProcessesByName("LeagueClient").FirstOrDefault()));
                lockfileloc = clientpath + @"\lockfile";

                //start the background task to poll for the lockfile
                tokenSource = new CancellationTokenSource();
                StartNewTask(CheckLockFileExists, TimeSpan.FromSeconds(0.5), tokenSource.Token);

                Console.WriteLine("Waiting for lockfile to be created...");
            }
        }

        //the lockfile contains the port number and password to connect with the Riot Games API
        async void CheckLockFileExists()
        {
            if (CheckClientIsOpen())
            {
                //check if lockfile has been generated
                if (File.Exists(lockfileloc))
                {
                    Console.WriteLine("lockfile has been created");
                    tokenSource.Cancel();

                    //open and read the lockfile
                    string lockfile = "";
                    using (FileStream fs = File.Open(lockfileloc, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        byte[] buf = new byte[1024];
                        int c;

                        while ((c = fs.Read(buf, 0, buf.Length)) > 0)
                        {
                            lockfile = Encoding.UTF8.GetString(buf, 0, c);
                        }
                    }

                    //parse the lockfile
                    port = lockfile.Split(':')[2];
                    password = lockfile.Split(':')[3];
                    //encode username and password in bytes
                    encoding = Encoding.ASCII.GetBytes($"riot:{password}");

                    //setup the http client
                    handler = new HttpClientHandler();
                    handler.ServerCertificateCustomValidationCallback = (requestMessage, certificate, chain, policyErrors) => true;
                    httpClient = new HttpClient(handler);

                    try
                    {
                        //get the summoner id of the logged in player from a json file
                        string summonerJson = await SendRequestAsync("GET", $"https://127.0.0.1:{port}/lol-summoner/v1/current-summoner", null);
                        //convert json file to an object using the SummonerInfo class
                        SummonerInfo summonerJsonObject = JsonConvert.DeserializeObject<SummonerInfo>(summonerJson);

                        summonerId = summonerJsonObject.summonerId;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error: " + ex.Message);
                    }

                    //start the background task to poll for champion select
                    tokenSource = new CancellationTokenSource();
                    StartNewTask(CheckInChampSelect, TimeSpan.FromSeconds(0.5), tokenSource.Token);
                }
            }
        }

        //the core part of this entire program, contains champion select logic
        async void CheckInChampSelect()
        {
            if (CheckClientIsOpen())
            {
                try
                {
                    //get the game state to check if champion select has started
                    string gamephase = await SendRequestAsync("GET", $"https://127.0.0.1:{port}/lol-gameflow/v1/gameflow-phase", null);

                    if (gamephase == "\"ChampSelect\"")
                    {
                        //check to make sure the available roles are not already displayed
                        if (!listedSets)
                        {
                            //get all rune pages the player has using the RunePageInfo class
                            string runePagesJson = await SendRequestAsync("GET", $"https://127.0.0.1:{port}/lol-perks/v1/pages", null);
                            List<RunePageInfo> runePageObject = JsonConvert.DeserializeObject<List<RunePageInfo>>(runePagesJson);

                            //get all item sets the player has the ItemSets class
                            string itemPagesJson = await SendRequestAsync("GET", $"https://127.0.0.1:{port}/lol-item-sets/v1/item-sets/{summonerId}/sets", null);
                            ItemSets itemPagesObject = JsonConvert.DeserializeObject<ItemSets>(itemPagesJson);

                            Action act = () => {
                                //retrieve the default rune page item, clear the rune page combobox (aka dropdown menu) of items then restore the default rune page item
                                ComboBoxItem defaultRunePage = (ComboBoxItem)window.RuneMenu.FindName("DefaultRunePage");
                                window.RuneMenu.Items.Clear();
                                window.RuneMenu.Items.Add(defaultRunePage);
                                window.RuneMenu.SelectedItem = defaultRunePage;

                                //add the names of the rune pages the player has to the combobox and store its id along with it
                                foreach (RunePageInfo runePage in runePageObject)
                                {
                                    if (runePage.isDeletable)
                                    {
                                        window.RuneMenu.Items.Add(new ComboBoxItem() { Content = runePage.name, Tag = runePage.id.ToString() });
                                    }
                                }
                                
                                //do the same tasks as above but for item pages
                                ComboBoxItem defaultItemPage = (ComboBoxItem)window.ItemMenu.FindName("DefaultItemPage");
                                window.ItemMenu.Items.Clear();
                                window.ItemMenu.Items.Add(defaultItemPage);
                                window.ItemMenu.SelectedItem = defaultItemPage;

                                foreach (ItemSet itemPage in itemPagesObject.itemSets)
                                {
                                    window.ItemMenu.Items.Add(new ComboBoxItem() { Content = itemPage.title, Tag = itemPage.uid });
                                }
                            };
                            window.Dispatcher.Invoke(act);

                            listedSets = true;
                        }
                        //get the id of the current champion selected using the ChampionHoverInfo class
                        string championHoverJson = await SendRequestAsync("GET", $"https://127.0.0.1:{port}/lol-champ-select/v1/session", null);
                        ChampionHoverInfo championHoverObject = JsonConvert.DeserializeObject<ChampionHoverInfo>(championHoverJson);

                        //select the champion id from searching for the current summoner from their team
                        championHoverId = championHoverObject.myTeam.FirstOrDefault(x => x.summonerId == summonerId).championId;

                        HtmlDocument htmlDoc = default;
                        string championLockId = default;
                        string primaryrole = default;
                        //check if a champion is selected
                        if (championHoverId != 0)
                        {
                            //get the name of the champion from its id using the ChampionInfo class
                            string championJson = await SendRequestAsync("GET", $"http://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/champions/{championHoverId}.json", null);
                            ChampionInfo championJsonObject = JsonConvert.DeserializeObject<ChampionInfo>(championJson);
                            //parse the name using regex
                            Regex regex = new Regex(@"[^A-Za-z0-9]+");
                                                      
                            //check if the champion hovered has changed
                            if (champion == default || champion != regex.Replace(championJsonObject.name, ""))
                            {
                                //assign the champion variable to the current champion
                                champion = regex.Replace(championJsonObject.name, "");

                                //load the statistics page from op.gg of the champion selected
                                htmlDoc = new HtmlWeb().Load($"https://na.op.gg/champion/{champion}/statistics/");

                                //XPath query that gets the available roles of the champion
                                string xpath = $"//ul[@class='champion-stats-position']//li";

                                //clear the roles grid of roles
                                window.Dispatcher.Invoke(new Action(() => window.RolesGrid.Children.Clear()));
                                //retrieve the roles from op.gg using the above XPath query
                                HtmlNodeCollection roles = htmlDoc.DocumentNode.SelectNodes(xpath);
                                
                                foreach (HtmlNode node in roles)
                                {
                                    //retrieve the assigned to the data-position element which contains the name of the role
                                    string role = node.GetAttributeValue("data-position", "nothing").ToLower();

                                    //set the first role as the primary role and the current role
                                    if (roles.IndexOf(node) == 0)
                                    {
                                        primaryrole = role;
                                        currole = role;
                                    }

                                    //add the role as a button to the UI
                                    AddRoleButton(role, roles.IndexOf(node) == 0);
                                }

                                //add the recommened summoner spells to the UI
                                DisplaySummoners(htmlDoc);

                                //display the champion icon and champion info overlay
                                Action act = () => {
                                    window.ChampionIcon.Source = new BitmapImage(new Uri($@"https://opgg-static.akamaized.net/images/lol/champion/{champion}.png?image=q_auto,w_140&v=1596679559", UriKind.Absolute));
                                
                                    if (window.WaitingOverlay.Visibility == Visibility.Visible)
                                    {
                                        window.WaitingOverlay.Visibility = Visibility.Collapsed;
                                        window.ChampionOverlay.Visibility = Visibility.Visible;
                                    }
                                };
                                window.Dispatcher.Invoke(act);

                                //get the id of the locked champion (if a champion was locked)
                                championLockId = await SendRequestAsync("GET", $"https://127.0.0.1:{port}/lol-champ-select/v1/current-champion", null);
                            }
                        }

                        //will change how this if block works later, this if block is supposed to automatically generate a rune page and item set if either or both of them weren't already "manually" generated
                        if (championLockId != "0" && championLockId != default && !champLocked)
                        {
                            Action act = () =>
                            {
                                window.WaitingOverlay.Visibility = Visibility.Visible;
                                window.ChampionOverlay.Visibility = Visibility.Collapsed;
                            };
                            window.Dispatcher.Invoke(act);

                            champLocked = true;
                                                      
                            string runePagesJson = await SendRequestAsync("GET", $"https://127.0.0.1:{port}/lol-perks/v1/pages", null);
                            List<RunePageInfo> runePageObject = JsonConvert.DeserializeObject<List<RunePageInfo>>(runePagesJson);

                            RunePage runePage = new RunePage(champion, primaryrole);

                            string runeJson = JsonConvert.SerializeObject(runePage, Formatting.Indented);

                            if (runePageObject.FirstOrDefault(x => x.name == champion + " " + CultureInfo.CurrentCulture.TextInfo.ToTitleCase(primaryrole.ToLower())) != null)
                            {
                                Console.WriteLine("Modifying rune page for " + champion + " " + CultureInfo.CurrentCulture.TextInfo.ToTitleCase(primaryrole.ToLower()));

                                int id = runePageObject.FirstOrDefault(x => x.name == champion + " " + CultureInfo.CurrentCulture.TextInfo.ToTitleCase(primaryrole.ToLower())).id;

                                await SendRequestAsync("PUT", $"https://127.0.0.1:{port}/lol-perks/v1/pages/{id}", runeJson);
                            }
                            else
                            {
                                await SendRequestAsync("POST", $"https://127.0.0.1:{port}/lol-perks/v1/pages/", runeJson);
                            }
                        }
                    }
                    //display the waiting overlay and reset values if champion select is exitted
                    if (gamephase != "\"ChampSelect\"")
                    {
                        Action act = () =>
                        {
                            if (window.ChampionOverlay.Visibility == Visibility.Visible)
                            {
                                window.ChampionOverlay.Visibility = Visibility.Collapsed;
                                window.WaitingOverlay.Visibility = Visibility.Visible;
                            }
                        };
                        window.Dispatcher.Invoke(act);

                        listedSets = false;
                        champion = default;
                        champLocked = false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error: " + ex.Message);
                }
            }
        }
        
        //displays summoner spells
        void DisplaySummoners(HtmlDocument htmlDoc)
        {
            //XPath query to get the path image source of each summoner spell
            string xpath = "//table[@class='champion-overview__table champion-overview__table--summonerspell']//img[contains(@src, 'Summoner')]";
            List<string> summonerImages = new List<string>();

            //for each image source, add https: in front of it to make it a link
            foreach (HtmlNode node in htmlDoc.DocumentNode.SelectNodes(xpath))
            {
                summonerImages.Add("https:" + node.GetAttributeValue("src", "nothing"));
            }
            //display each summoner spell as an image retrieved from the links made above
            Action act = () =>
            {
                window.Summoner1Image.Source = new BitmapImage(new Uri(summonerImages[0], UriKind.Absolute));
                window.Summoner2Image.Source = new BitmapImage(new Uri(summonerImages[1], UriKind.Absolute));
                window.Summoner3Image.Source = new BitmapImage(new Uri(summonerImages[2], UriKind.Absolute));
                window.Summoner4Image.Source = new BitmapImage(new Uri(summonerImages[3], UriKind.Absolute));
            };
            window.Dispatcher.Invoke(act);
        }

        //handles when the user selects a different role
        void SelectRole(object sender, RoutedEventArgs e)
        {
            //get the current button selected
            Button currbutton = sender as Button;

            foreach (UIElement children in window.RolesGrid.Children)
            {
                //get the image associated with each button
                Button button = children as Button;
                Image image = button.Content as Image;

                //"dim" the other images and "brighten" the current selected one
                if (button != currbutton)
                {
                    window.Dispatcher.Invoke(new Action(() => image.Opacity = 0.25));
                }
                else
                {
                    window.Dispatcher.Invoke(new Action(() => image.Opacity = 1));
                    currole = button.Name;
                }
            }

            //display the new summoner spells associated with the new role selected
            DisplaySummoners(new HtmlWeb().Load($"https://na.op.gg/champion/{champion}/statistics/{currbutton.Name}"));
        }

        //adds a role button to the UI
        void AddRoleButton(string role, bool primary)
        {
            //dictionary of the images of each role
            Dictionary<string, string> imagePaths = new Dictionary<string, string>()
            {
                {"top",  "https://ultimate-bravery.net/images/roles/top_icon.png"},
                {"jungle",  "https://ultimate-bravery.net/images/roles/jungle_icon.png"},
                {"mid",  "https://ultimate-bravery.net/images/roles/mid_icon.png"},
                {"adc", "https://ultimate-bravery.net/images/roles/bot_icon.png"},
                {"support", "https://ultimate-bravery.net/images/roles/support_icon.png"}
            };

            //create a new button for the new role
            Current.Dispatcher.Invoke(delegate
            {
                string image = imagePaths[role];
                               
                Button newBtn = new Button()
                {
                    Name = role,
                    Content = new Image
                    {
                        Source = new BitmapImage(new Uri(image)),
                        //"brighten" image if this is the primary role
                        Opacity = (primary) ? 1 : 0.25
                    },
                    Height = 30,
                    Width = 30,
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(-4)
                };
                newBtn.Click += new RoutedEventHandler(SelectRole);

                //add the new button to the roles grid
                window.RolesGrid.Children.Add(newBtn);
            });
        }

        //creates a rune page according to the champion and role
        async void GenerateRunePage(string champion, string role, int id)
        {
            //create and aggregate a new rune page object
            RunePage runePage = new RunePage(champion, role);

            //check if the current rune page selected in the combobox is the default one
            if ((string)((ComboBoxItem)window.RuneMenu.SelectedItem).Content != "Default")
            {
                //update the name of the rune page if it is not
                ((ComboBoxItem)window.RuneMenu.SelectedItem).Content = runePage.name;
            }
            else
            {
                //specify in the name that it is default if it is
                runePage.name += " (Default)";
            }

            //convert the rune page object into a json file
            string runeJson = JsonConvert.SerializeObject(runePage, Formatting.Indented);

            //check if the rune page id has not been assigned, if it has not, create a new page
            if (id == default)
            {
                //send a post request to upload the new rune page
                await SendRequestAsync("POST", $"https://127.0.0.1:{port}/lol-perks/v1/pages/", runeJson);

                //get the id of the uploaded rune page using the RunePageInfo class
                string currentRunePageJson = await SendRequestAsync("GET", $"https://127.0.0.1:{port}/lol-perks/v1/currentpage", null);
                RunePageInfo currentRunePageObject = JsonConvert.DeserializeObject<RunePageInfo>(currentRunePageJson);

                //assign the new id to the default rune page id setting and the current combobox item tag which contains the id of the rune page
                LoL_Generator.Properties.Settings.Default.runePageID = currentRunePageObject.id;
                LoL_Generator.Properties.Settings.Default.Save();
                ((ComboBoxItem)window.RuneMenu.SelectedItem).Tag = currentRunePageObject.id;
            }
            else
            {
                //if the id has been assigned send a put request to update the rune page
                await SendRequestAsync("PUT", $"https://127.0.0.1:{port}/lol-perks/v1/pages/{id}", runeJson);
            }
        }

        //still in testing, creates an item page (similar logic as with rune page generating)
        async void GenerateItemPage(string champion, string role, string uid)
        {
            ItemSet itemSet = new ItemSet(champion, role, championHoverId);

            if ((string)((ComboBoxItem)window.ItemMenu.SelectedItem).Content != "Default")
            {
                ((ComboBoxItem)window.ItemMenu.SelectedItem).Content = itemSet.title;
            }
            else
            {
                itemSet.title += " (Default)";
            }

            string itemPagesJson = await SendRequestAsync("GET", $"https://127.0.0.1:{port}/lol-item-sets/v1/item-sets/{summonerId}/sets", null);
            ItemSets itemPagesObject = JsonConvert.DeserializeObject<ItemSets>(itemPagesJson);

            if (uid == default)
            {
                itemPagesObject.itemSets.Add(new ItemSet(champion, role, championHoverId));
            }
            else
            {
                int index = itemPagesObject.itemSets.FindIndex(x => x.uid == uid);
                itemPagesObject.itemSets[index] = itemSet;
            }

            string itemsetsJson = JsonConvert.SerializeObject(itemPagesObject, Formatting.Indented);

            await SendRequestAsync("POST", $"https://127.0.0.1:{port}/lol-item-sets/v1/item-sets/{summonerId}/sets", itemsetsJson);
        }

        //function that is attached to the generate loadout button, calls functions to generate a rune page and item set for the current champion and selected role
        void GenerateLoadout(object sender, RoutedEventArgs e)
        {
            GenerateRunePage(champion, currole, (int)((ComboBoxItem)window.RuneMenu.SelectedItem).Tag);
            //GenerateItemPage(champion, currole, (string)((ComboBoxItem)window.ItemMenu.SelectedItem).Tag);
        }

        //sends REST API requests and returns data if applicable
        async Task<string> SendRequestAsync(string method, string url, string json)
        {
            //initiate the new request using the method and url parameters
            using (HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(method), url))
            {
                //specify we want to request json data
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                //specify authorization details
                request.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(encoding));

                //specifiy json data to be uploaded if the method is a POST or PUT request
                if (method == "POST" || method == "PUT")
                {
                    request.Content = new StringContent(json);
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                }

                //send the request
                HttpResponseMessage response = await httpClient.SendAsync(request);

                //return data if it is a GET request
                if (method == "GET")
                {
                    return await response.Content.ReadAsStringAsync();
                }
            }

            return null;
        }

        //starts a new background task primarily using the function to be looped and the time interval
        //code retrieved from: https://stackoverflow.com/questions/7472013/how-to-create-a-thread-task-with-a-continuous-loop/35308832
        static void StartNewTask(Action action, TimeSpan pollInterval, CancellationToken token, TaskCreationOptions taskCreationOptions = TaskCreationOptions.None)
        {
            Task.Factory.StartNew(
                () =>
                {
                    do
                    {
                        try
                        {
                            action();
                            if (token.WaitHandle.WaitOne(pollInterval)) break;
                        }
                        catch
                        {
                            return;
                        }
                    }
                    while (true);
                },
                token,
                taskCreationOptions,
                TaskScheduler.Default);
        }

        //functions to help retrieve the file names of running processes without needing elevated rights
        //code retrieved from: https://stackoverflow.com/questions/8431298/process-mainmodule-access-is-denied
        [Flags]
        private enum ProcessAccessFlags : uint
        {
            QueryLimitedInformation = 0x00001000
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryFullProcessImageName(
              [In] IntPtr hProcess,
              [In] int dwFlags,
              [Out] StringBuilder lpExeName,
              ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
         ProcessAccessFlags processAccess,
         bool bInheritHandle,
         int processId);

        static String GetProcessFilename(Process p)
        {
            int capacity = 2000;
            StringBuilder builder = new StringBuilder(capacity);

            IntPtr ptr = OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, p.Id);

            if (!QueryFullProcessImageName(ptr, 0, builder, ref capacity))
            {
                return String.Empty;
            }

            return builder.ToString();
        }
    }

    //classes to parse json data
    public class SummonerInfo
    {
        public long summonerId;
    }

    public class MyTeam
    {
        public long summonerId;
        public int championId;
    }

    public class ChampionHoverInfo
    {
        public List<MyTeam> myTeam;
    }

    public class ChampionInfo
    {
        public int id;
        public string name;
    }

    public class RunePageInfo
    {
        public int id;
        public string name;
        public bool isDeletable;
    }

    public class ItemSets
    {
        public long accountId;
        public List<ItemSet> itemSets;
    }

}



