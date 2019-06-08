using Discord.WebSocket;
using IPSClient;
using IPSClient.Objects.Gallery;
using Newtonsoft.Json;
using Smeargle.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Smeargle
{
    class Program
    {
        private static InvisionCommunityConfiguration ipsConfig;
        private static DiscordConnectionInfo discordConfig;
        
        private static DiscordSocketClient discordClient;

        private static ApiClient ipsClient;
        private static Dictionary<string, int> albumsByPokemon;
        private static Dictionary<int, List<string>> imagesByAlbum;
        private static SemaphoreSlim DictionaryLoadLock = new SemaphoreSlim(1);
        private static SemaphoreSlim ImageDownloadLock = new SemaphoreSlim(1);

        private static Random random = new Random();
        private static HttpClient httpClient = new HttpClient();

        static async Task Main(string[] args)
        {
            ipsConfig = JsonConvert.DeserializeObject<InvisionCommunityConfiguration>(File.ReadAllText("ips.config"));
            discordConfig = JsonConvert.DeserializeObject<DiscordConnectionInfo>(File.ReadAllText("discord.config"));

            Console.WriteLine("Loading stuff from forum");
            ipsClient = new ApiClient(ipsConfig.BaseUrl, ipsConfig.ApiKey);
            LoadAlbums();

            Console.WriteLine("Loading stuff from discord");
            discordClient = new DiscordSocketClient();
            await discordClient.LoginAsync(Discord.TokenType.Bot, discordConfig.Token);
            discordClient.MessageReceived += Discord_MessageReceived;
            await discordClient.StartAsync();

            Console.WriteLine("Waiting forever");
            await Task.Delay(Timeout.Infinite);
        }

        private static async Task Discord_MessageReceived(SocketMessage message)
        {
            if (message.Channel.Id != discordConfig.ChannelId || message.Author.Username == discordClient.CurrentUser.Username)
            {
                return;
            }

            Console.WriteLine($"#{message.Channel.Name}: [{message.Author.Username}] {message.Content}");

            if (message.Content == "!ping")
            {
                await message.Channel.SendMessageAsync("Pong!");
            }
            else if (message.Content == "!ding")
            {
                await message.Channel.SendMessageAsync("Dong!");
            }
            else if (message.Content.StartsWith("!"))
            {
                if (albumsByPokemon.ContainsKey(message.Content.TrimStart('!')))
                {
                    var albumId = albumsByPokemon[message.Content.TrimStart('!')];
                    var url = await GetRandomAlbumImageUrl(albumId);
                    var localPath = Path.Combine("cache", Path.GetFileName(url));
                    if (!File.Exists(localPath))
                    {
                        await ImageDownloadLock.WaitAsync();
                        try
                        {
                            var response = await httpClient.GetAsync(url);
                            var responseData = await response.Content.ReadAsByteArrayAsync();
                            if (!Directory.Exists("cache"))
                            {
                                Directory.CreateDirectory("cache");
                            }
                            File.WriteAllBytes(localPath, responseData);
                        }
                        finally
                        {
                            ImageDownloadLock.Release();
                        }
                    }

                    var data = File.ReadAllBytes(localPath);
                    if (data.Length < 8 * 1024 * 1024)
                    {
                        await message.Channel.SendFileAsync(localPath);
                    }
                    else
                    {
                        await message.Channel.SendMessageAsync(url);
                    }
                }
            }
        }

        private static void LoadAlbums()
        {
            var albums = ipsClient.GetAlbums(new GetAlbumsRequest { categories = ipsConfig.GalleryCategoryId.ToString() }).ToList();
            albumsByPokemon = new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase);
            imagesByAlbum = new Dictionary<int, List<string>>();
            foreach (var album in albums)
            {
                // Album names are in the form "001 Bulbasaur", "447 Riolu", etc.
                if (album.name.Contains(" "))
                {
                    albumsByPokemon.TryAdd(album.name.Split(' ')[1], album.id);
                }
            }
        }

        private static async Task<string> GetRandomAlbumImageUrl(int albumId)
        {
            // To-do: invalidate our cache after a while
            if (!imagesByAlbum.ContainsKey(albumId))
            {
                await DictionaryLoadLock.WaitAsync();
                try
                {
                    if (!imagesByAlbum.ContainsKey(albumId))
                    {
                        var images = ipsClient.GetImages(new GetImagesRequest { albums = albumId.ToString() });
                        imagesByAlbum.Add(albumId, images.Select(i => i.images.original).ToList());
                    }
                }
                finally
                {
                    DictionaryLoadLock.Release();
                }
            }

            var set = imagesByAlbum[albumId];
            return set[random.Next(0, set.Count - 1)];
        }
    }
}
