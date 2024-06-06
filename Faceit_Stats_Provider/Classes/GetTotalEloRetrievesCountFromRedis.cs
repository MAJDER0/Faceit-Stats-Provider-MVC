﻿using Faceit_Stats_Provider.Models;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using StackExchange.Redis;
using System;
using System.Linq;
using System.Threading.Tasks;

public class GetTotalEloRetrievesCountFromRedis
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public GetTotalEloRetrievesCountFromRedis(IConfiguration configuration, IConnectionMultiplexer redis)
    {
        _redis = redis;
        _db = _redis.GetDatabase();
    }

    public async Task<long> GetTotalEloRetrievesCountFromRedisAsync(string playerId)
    {
        try
        {
            var server = _redis.GetServer(_redis.GetEndPoints().First());
            var keys = server.Keys(pattern: $"*{playerId}*").ToArray();

            Console.WriteLine($"Found {keys.Length} keys for playerId {playerId}");

            long totalCount = 0;
            foreach (var key in keys)
            {
                try
                {
                    var keyType = await _db.KeyTypeAsync(key);
                    if (keyType == RedisType.String)
                    {
                        var jsonString = await _db.StringGetAsync(key);
                        var matchData = JsonConvert.DeserializeObject<List<RedisMatchData.MatchData>>(jsonString);

                        if (matchData != null)
                        {
                            Console.WriteLine($"Key: {key}, Count: {matchData.Count}");
                            totalCount += matchData.Count;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Key: {key} is not a string, it is of type: {keyType}");
                    }
                }
                catch (Exception innerEx)
                {
                    Console.WriteLine($"An error occurred while processing key {key}: {innerEx.Message}");
                }
            }

            return totalCount;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}");
            return 0; // or handle as appropriate
        }
    }
}
