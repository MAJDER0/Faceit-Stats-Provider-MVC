﻿using Faceit_Stats_Provider.Classes;
using Faceit_Stats_Provider.Interfaces;
using Faceit_Stats_Provider.Models;
using Faceit_Stats_Provider.ModelsForAnalyzer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace Faceit_Stats_Provider.Controllers
{
    public class AnalyzerController : Controller
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IMemoryCache _memoryCache;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

        public AnalyzerController(IHttpClientFactory clientFactory, IMemoryCache memoryCache)
        {
            _clientFactory = clientFactory;
            _memoryCache = memoryCache;
        }

        public IActionResult Index()
        {
            return View("~/Views/Analyzer/AnalyzerSearch.cshtml");
        }

        [HttpGet]
        public async Task<ActionResult> Analyze(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                return View("~/Views/InvalidMatchRoomLink/InvalidMatchRoomLink.cshtml");
            }

            var client = _clientFactory.CreateClient("Faceit");
            string RoomID = ExtractRoomIdFromUrl(roomId);

            AnalyzerMatchPlayers.Rootobject players;
            var getPlayerStatsTasks = new List<Task<AnalyzerPlayerStats.Rootobject>>();
            var getPlayerStatsForCsGoTasks = new List<Task<AnalyzerPlayerStatsForCsgo.Rootobject>>();
            var getPlayerMatchHistoryTasks = new List<(string playerId, Task<AnalyzerMatchHistory.Rootobject>)>();
            var getPlayerMatchStatsTasks = new List<(string playerId, Task<AnalyzerMatchStats.Rootobject>)>();

            try
            {
                players = await GetOrAddToCacheAsync($"players_{RoomID}", () => client.GetFromJsonAsync<AnalyzerMatchPlayers.Rootobject>($"v4/matches/{RoomID}"));

                foreach (var item in players.teams.faction1.roster)
                {
                    getPlayerStatsTasks.Add(GetOrAddToCacheAsync($"stats_cs2_{item.player_id}", () => client.GetFromJsonAsync<AnalyzerPlayerStats.Rootobject>($"v4/players/{item.player_id}/stats/cs2")));
                    getPlayerStatsForCsGoTasks.Add(GetOrAddToCacheAsync($"stats_csgo_{item.player_id}", () => client.GetFromJsonAsync<AnalyzerPlayerStatsForCsgo.Rootobject>($"v4/players/{item.player_id}/stats/csgo")));
                    getPlayerMatchHistoryTasks.Add((item.player_id, GetOrAddToCacheAsync($"history_cs2_{item.player_id}", () => client.GetFromJsonAsync<AnalyzerMatchHistory.Rootobject>($"v4/players/{item.player_id}/history?game=cs2&from=120&offset=0&limit=20"))));
                }

                foreach (var item in players.teams.faction2.roster)
                {
                    getPlayerStatsTasks.Add(GetOrAddToCacheAsync($"stats_cs2_{item.player_id}", () => client.GetFromJsonAsync<AnalyzerPlayerStats.Rootobject>($"v4/players/{item.player_id}/stats/cs2")));
                    getPlayerStatsForCsGoTasks.Add(GetOrAddToCacheAsync($"stats_csgo_{item.player_id}", () => client.GetFromJsonAsync<AnalyzerPlayerStatsForCsgo.Rootobject>($"v4/players/{item.player_id}/stats/csgo")));
                    getPlayerMatchHistoryTasks.Add((item.player_id, GetOrAddToCacheAsync($"history_cs2_{item.player_id}", () => client.GetFromJsonAsync<AnalyzerMatchHistory.Rootobject>($"v4/players/{item.player_id}/history?game=cs2&from=120&offset=0&limit=20"))));
                }

                // Await all tasks concurrently
                await Task.WhenAll(getPlayerStatsTasks);
                await Task.WhenAll(getPlayerStatsForCsGoTasks);
                await Task.WhenAll(getPlayerMatchHistoryTasks.Select(t => t.Item2));

                // Retrieve results from tasks
                var playerStats = getPlayerStatsTasks.Select(t => t.Result).ToList();
                var playerStatsForCsGo = getPlayerStatsForCsGoTasks.Select(t => t.Result).ToList();
                var playerMatchHistory = getPlayerMatchHistoryTasks.Select(t => (t.playerId, t.Item2.Result)).ToList();

                foreach (var (playerId, playerHistory) in playerMatchHistory)
                {
                    foreach (var matchItem in playerHistory.items)
                    {
                        getPlayerMatchStatsTasks.Add((playerId, GetOrAddToCacheAsync($"match_stats_{matchItem.match_id}", () => client.GetFromJsonAsync<AnalyzerMatchStats.Rootobject>($"v4/matches/{matchItem.match_id}/stats"))));
                    }
                }

                var playerMatchStatsResults = await Task.WhenAll(getPlayerMatchStatsTasks.Select(task => HandleHttpRequestAsync(task.Item2).ContinueWith(t => (task.playerId, Result: t.Result))));
                var playerMatchStats = playerMatchStatsResults.Where(result => result.Result != null).ToList();

                // Create deep copy of the initial model
                var initialViewModel = new AnalyzerViewModel
                {
                    RoomId = RoomID,
                    Players = players,
                    PlayerStats = playerStats,
                    PlayerStatsForCsGo = playerStatsForCsGo,
                    PlayerMatchStats = playerMatchStats
                };

                var initialModelCopy = JsonConvert.DeserializeObject<AnalyzerViewModel>(JsonConvert.SerializeObject(initialViewModel));

                var viewModel = new AnalyzerViewModel
                {
                    RoomId = RoomID,
                    Players = players,
                    PlayerStats = playerStats,
                    PlayerStatsForCsGo = playerStatsForCsGo,
                    PlayerMatchStats = playerMatchStats,
                    InitialModelCopy = ModelMapper.ToExcludePlayerModel(initialModelCopy) // Use mapping function
                };

                return View("~/Views/Analyzer/Analyze.cshtml", viewModel);
            }
            catch (Exception ex)
            {
                return View("~/Views/InvalidMatchRoomLink/InvalidMatchRoomLink.cshtml");
            }
        }

        [HttpPost]
        public ActionResult TogglePlayer([FromBody] ExcludePlayerModel model)
        {
            if (model == null)
            {
                return BadRequest("Model is null");
            }

            if (model.Players == null || model.PlayerStats == null || model.PlayerMatchStats == null)
            {
                return BadRequest("Required data is missing");
            }

            var initialModelCopy = model.InitialModelCopy;
            var players = model.Players;
            var excludedPlayerIds = model.ExcludedPlayers;

            // Exclude the players from the model
            if (excludedPlayerIds != null && excludedPlayerIds.Count > 0)
            {
                if (players.teams.faction1?.roster != null)
                {
                    players.teams.faction1.roster = players.teams.faction1.roster.Where(p => !excludedPlayerIds.Contains(p.player_id)).ToArray();
                }

                if (players.teams.faction2?.roster != null)
                {
                    players.teams.faction2.roster = players.teams.faction2.roster.Where(p => !excludedPlayerIds.Contains(p.player_id)).ToArray();
                }

                var playerStats = model.PlayerStats?.Where(ps => !excludedPlayerIds.Contains(ps.player_id)).ToList();
                var playerMatchStats = model.PlayerMatchStats?
                    .Where(pms => !excludedPlayerIds.Contains(pms.playerId))
                    .Select(pms => (pms.playerId, pms.matchStats))
                    .ToList();

                var result = StatsHelper.CalculateNeededStatistics(players.teams.faction1.leader, players.teams.faction2.leader, players.teams.faction1.roster, players.teams.faction2.roster, playerStats, playerMatchStats);
                var CombinedPlayerStats = result.Item8.Concat(result.Item9).ToList();

                var modifiedViewModel = new AnalyzerViewModel
                {
                    RoomId = model.RoomId,
                    Players = players,
                    PlayerStats = CombinedPlayerStats,
                    PlayerMatchStats = playerMatchStats,
                    InitialModelCopy = initialModelCopy // Already of type ExcludePlayerModel
                };

                var partialViewModel = new AnalyzerPartialViewModel
                {
                    ModifiedViewModel = modifiedViewModel,
                    OriginalViewModel = ModelMapper.ToAnalyzerViewModel(initialModelCopy) // Use mapping function
                };

                return PartialView("_StatisticsPartial", partialViewModel);
            }
            else
            {
                // Restore the original view model
                var restoredPlayers = initialModelCopy.Players;
                var restoredPlayerStats = initialModelCopy.PlayerStats;
                var restoredPlayerMatchStats = initialModelCopy.PlayerMatchStats.Select(pms => (pms.playerId, pms.matchStats)).ToList();

                var result = StatsHelper.CalculateNeededStatistics(restoredPlayers.teams.faction1.leader, restoredPlayers.teams.faction2.leader, restoredPlayers.teams.faction1.roster, restoredPlayers.teams.faction2.roster, restoredPlayerStats, restoredPlayerMatchStats);
                var CombinedPlayerStats = result.Item8.Concat(result.Item9).ToList();

                var modifiedViewModel = new AnalyzerViewModel
                {
                    RoomId = model.RoomId,
                    Players = restoredPlayers,
                    PlayerStats = CombinedPlayerStats,
                    PlayerMatchStats = restoredPlayerMatchStats,
                    InitialModelCopy = initialModelCopy // Already of type ExcludePlayerModel
                };

                var partialViewModel = new AnalyzerPartialViewModel
                {
                    ModifiedViewModel = modifiedViewModel,
                    OriginalViewModel = ModelMapper.ToAnalyzerViewModel(initialModelCopy) // Use mapping function
                };

                return PartialView("_StatisticsPartial", partialViewModel);
            }
        }

        private string ExtractRoomIdFromUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                string[] segments = uri.Segments;
                // The room ID is always the last segment if there's no additional path, or the second-to-last if there is
                if (segments.Length >= 4 && segments[segments.Length - 2].Equals("room/", StringComparison.OrdinalIgnoreCase))
                {
                    return segments[segments.Length - 1].Trim('/');
                }
                else if (segments.Length > 4)
                {
                    return segments[segments.Length - 2].Trim('/');
                }
            }
            return null;
        }

        private async Task<T> GetOrAddToCacheAsync<T>(string cacheKey, Func<Task<T>> factory)
        {
            if (!_memoryCache.TryGetValue(cacheKey, out T cacheEntry))
            {
                cacheEntry = await factory();
                var cacheEntryOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = CacheDuration
                };
                _memoryCache.Set(cacheKey, cacheEntry, cacheEntryOptions);
            }
            return cacheEntry;
        }

        private async Task<T> HandleHttpRequestAsync<T>(Task<T> task)
        {
            try
            {
                return await task;
            }
            catch (HttpRequestException ex)
            {
                // Log the specific request that failed
                // For example, you can use a logging framework or output to console
                Console.WriteLine($"HTTP request error: {ex.Message}");
                return default;
            }
            catch (Exception ex)
            {
                // Log other exceptions
                Console.WriteLine($"General error: {ex.Message}");
                return default;
            }
        }
    }
}
