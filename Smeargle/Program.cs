using Discord.WebSocket;
using IPSClient;
using IPSClient.Objects.Gallery;
using Newtonsoft.Json;
using Smeargle.Configuration;
using System;
using System.Collections.Concurrent;
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
        private static readonly Random random = new Random();
        private static readonly HttpClient httpClient = new HttpClient();

        private static InvisionCommunityConfiguration ipsConfig = default!;
        private static DiscordConnectionInfo discordConfig = default!;
        
        private static DiscordSocketClient discordClient = default!;
        private static ManualResetEventSlim disconnectedEvent = new ManualResetEventSlim(false);

        private static ApiClient ipsClient = default!;
        private static ConcurrentDictionary<string, int> albumsByPokemon = default!;
        private static Dictionary<int, List<string>>? imagesByAlbum;
        private static SemaphoreSlim DictionaryLoadLock = new SemaphoreSlim(1);
        private static SemaphoreSlim ImageDownloadLock = new SemaphoreSlim(1);

        private static Timer? albumsRefreshTimer;

        static async Task Main(string[] args)
        {
            ipsConfig = JsonConvert.DeserializeObject<InvisionCommunityConfiguration>(File.ReadAllText("ips.config")) ?? throw new Exception("Failed to deserialize ips.config");
            discordConfig = JsonConvert.DeserializeObject<DiscordConnectionInfo>(File.ReadAllText("discord.config")) ?? throw new Exception("Failed to deserialize discord.config");

            Console.WriteLine("Loading stuff from forum");
            ipsClient = new ApiClient(ipsConfig.BaseUrl, ipsConfig.ApiKey);
            LoadAlbums();

            Console.WriteLine("Loading stuff from discord");
            discordClient = new DiscordSocketClient();
            await discordClient.LoginAsync(Discord.TokenType.Bot, discordConfig.Token);
            discordClient.MessageReceived += Discord_MessageReceived;
            discordClient.Disconnected += Discord_Disconnected;
            await discordClient.StartAsync();

            Console.WriteLine("Waiting until disconnected");
            disconnectedEvent.Wait();
        }

        private static async Task Discord_MessageReceived(SocketMessage message)
        {
            if (message.Author.Username == discordClient.CurrentUser.Username)
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
            else if (message.Content.StartsWith("!random", StringComparison.OrdinalIgnoreCase))
            {
                var randomPokemonName = albumsByPokemon.Skip(random.Next(0, albumsByPokemon.Count)).First().Key;
                await Discord_PostPokemon(randomPokemonName, message);
            }
            else if (message.Content.StartsWith("!"))
            {
                var pokemonName = message.Content.TrimStart('!');
                if (albumsByPokemon.ContainsKey(pokemonName))
                {
                    await Discord_PostPokemon(pokemonName, message);
                }
            }
        }

        private static async Task Discord_PostPokemon(string pokemonName, SocketMessage message)
        {
            var albumId = albumsByPokemon[pokemonName];
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

        private static Task Discord_Disconnected(Exception ex)
        {
            Console.WriteLine("Disconnected with exception: " + ex.ToString());
            disconnectedEvent.Set();
            return Task.CompletedTask;
        }
         
        private static void LoadAlbums()
        {
            albumsByPokemon = new ConcurrentDictionary<string, int>(StringComparer.InvariantCultureIgnoreCase);

            RefreshAlbums();

            const int oneHourInMilliseconds = 60 * 60 * 1000 /* 1 hour */;
            albumsRefreshTimer = new Timer(new TimerCallback(_ => RefreshAlbums()), state: null, dueTime: oneHourInMilliseconds, period: oneHourInMilliseconds);
        }

        private static void RefreshAlbums()
        {
            var albums = ipsClient.GetAlbums(new GetAlbumsRequest { categories = ipsConfig.GalleryCategoryId.ToString() }).ToList();
            foreach (var album in albums)
            {
                // Album names are in the form "001 Bulbasaur", "447 Riolu", etc.
                if (album.name.Contains(" "))
                {
                    albumsByPokemon[album.name.Split(' ', 2)[1]] = album.id;
                }
                else
                {
                    albumsByPokemon[album.name] = album.id;
                }
            }

            if (imagesByAlbum != null)
            {
                DictionaryLoadLock.Wait();
                try
                {
                    imagesByAlbum = null;
                }
                finally
                {
                    DictionaryLoadLock.Release();
                }
            }
        }

        private static async Task<string> GetRandomAlbumImageUrl(int albumId)
        {
            // To-do: invalidate our cache after a while
            if (imagesByAlbum == null || !imagesByAlbum.ContainsKey(albumId))
            {
                await DictionaryLoadLock.WaitAsync();
                try
                {
                    imagesByAlbum ??= new Dictionary<int, List<string>>();
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
