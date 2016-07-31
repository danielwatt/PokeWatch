﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Google.Protobuf.Collections;
using Pokewatch.Datatypes;
using Pokewatch.DataTypes;
using POGOLib.Net;
using POGOLib.Net.Authentication;
using POGOLib.Pokemon;
using POGOLib.Pokemon.Data;
using POGOProtos.Enums;
using POGOProtos.Map;
using POGOProtos.Map.Pokemon;
using Tweetinvi;
using Tweetinvi.Core.Extensions;
using Tweetinvi.Models;
using Location = Pokewatch.Datatypes.Location;

namespace Pokewatch
{
    public class Program
    {
        public static void Main(string[] args)
        {
            try
            {
                string json = File.ReadAllText("Configuration.json");
                s_config = new JavaScriptSerializer().Deserialize<Configuration>(json);
            }
            catch (Exception ex)
            {
                Log("[-]Unable to load config.");
                Log(ex.Message);
                return;
            }

            listLocations();
            excludedPokemonList();

            if ((s_config.PTCUsername.IsNullOrEmpty() || s_config.PTCPassword.IsNullOrEmpty()) && (s_config.GAPassword.IsNullOrEmpty() || s_config.GAUsername.IsNullOrEmpty()))
            {
                Log("[-]Username and password must be supplied for either PTC or Google.");
                return;
            }

            if (!PrepareTwitterClient())
                return;

            Log("[+]Sucessfully signed in to twitter.");
            if (PrepareClient())
            {
                Log("[+]Sucessfully signed in to PokemonGo, beginning search.");
            }
            else
            {
                Environment.Exit(1);
            };

            if (!Search())
                throw new Exception();
        }

        private static bool Search()
        {
            List<FoundPokemon> tweetedPokemon = new List<FoundPokemon>();
            DateTime lastTweet = DateTime.MinValue;
            Random random = new Random();
            while (true)
            {
                int regionIndex = random.Next(s_config.Regions.Count);
                if (regionIndex == s_config.Regions.Count)
                    regionIndex = 0;

                Region region = s_config.Regions[regionIndex];
                Log($"[!]Searching Region: {region.Name}");
                foreach (Location location in region.Locations)
                {
                    SetLocation(location);

                    //Wait so we don't clobber api and to let the heartbeat catch up to our new location. (Minimum heartbeat time is 4000ms)
                    Thread.Sleep(5000);
                    Log("[!]Searching nearby cells.");
                    if (!tweetedPokemon.IsEmpty())
                    {
                        for (int i = 0; i < tweetedPokemon.Count; i++) {
                            if (tweetedPokemon.ElementAt(i).ExpirationTime < DateTime.Now.ToLocalTime())
                            {
                                Log("[!]Tweeted pokemon " + tweetedPokemon.ElementAt(i).Kind + " has expired. Bye!");
                                tweetedPokemon.RemoveAt(i);                               
                            }
                        }
                    }
                    RepeatedField<MapCell> mapCells;
                    try
                    {
                        mapCells = s_pogoSession.Map.Cells;
                    }
                    catch
                    {
                        Log("[-]Heartbeat has failed. Terminating Connection.");
                        return false;
                    }
                    foreach (var mapCell in mapCells)
                    {
                        foreach (WildPokemon pokemon in mapCell.WildPokemons)
                        {
                            FoundPokemon foundPokemon = ProcessPokemon(pokemon, tweetedPokemon, lastTweet);

                            if (foundPokemon == null)
                                continue;

                            string tweet = ComposeTweet(foundPokemon, region);

                            try
                            {
                                s_twitterClient.PublishTweet(tweet);
                            }
                            catch (Exception ex)
                            {
                                Log("[-] Error" + ex.GetHashCode() + " " + "Tweet failed to publish: " + tweet + " " + ex.Message);
                                continue;
                            }

                            Log("[+]Tweet published: " + tweet);
                            lastTweet = DateTime.Now;

                            tweetedPokemon.Add(foundPokemon);
                            if (tweetedPokemon.Count > 10)
                                tweetedPokemon.RemoveAt(0);
                        }
                    }
                }
                Log("[!]Finished Searching " + region.Name);
            }
        }

        //Sign in to PokemonGO
        private static bool PrepareClient()
        {
            Location defaultLocation;
            try
            {
                defaultLocation = s_config.Regions.First().Locations.First();
            }
            catch
            {
                Log("[-]No locations have been supplied.");
                return false;
            }
            if (!s_config.PTCUsername.IsNullOrEmpty() && !s_config.PTCPassword.IsNullOrEmpty())
            {
                try
                {
                    Log("[!]Attempting to sign in to PokemonGo using PTC.");
                    s_pogoSession = Login.GetSession(s_config.PTCUsername, s_config.PTCPassword, LoginProvider.PokemonTrainerClub, defaultLocation.Latitude, defaultLocation.Longitude);
                    Log("[+]Sucessfully logged in to PokemonGo using PTC.");
                    return true;
                }
                catch
                {
                    Log("[-]Unable to log in using PTC.");
                }
            }
            if (!s_config.GAUsername.IsNullOrEmpty() && !s_config.GAPassword.IsNullOrEmpty())
            {
                try
                {
                    Log("[!]Attempting to sign in to PokemonGo using Google.");
                    s_pogoSession = Login.GetSession(s_config.GAUsername, s_config.GAPassword, LoginProvider.GoogleAuth, defaultLocation.Latitude, defaultLocation.Longitude);
                    Log("[+]Sucessfully logged in to PokemonGo using Google.");
                    return true;
                }
                catch
                {
                    Log("[-]Unable to log in using Google.");
                }
            }
            return false;
        }

        //Sign in to Twitter.
        private static bool PrepareTwitterClient()
        {
            if (s_config.TwitterConsumerToken.IsNullOrEmpty() || s_config.TwitterConsumerSecret.IsNullOrEmpty()
                || s_config.TwitterAccessToken.IsNullOrEmpty() || s_config.TwitterConsumerSecret.IsNullOrEmpty())
            {
                Log("[-]Must supply Twitter OAuth strings.");
                return false;
            }

            Log("[!]Signing in to Twitter.");
            var userCredentials = Auth.CreateCredentials(s_config.TwitterConsumerToken, s_config.TwitterConsumerSecret, s_config.TwitterAccessToken, s_config.TwitterAccessSecret);
            ExceptionHandler.SwallowWebExceptions = false;
            try
            {
                s_twitterClient = User.GetAuthenticatedUser(userCredentials);
            }
            catch
            {
                Log("[-]Unable to authenticate Twitter account. Check your internet connection, verify your OAuth credential strings. If your bot is new, Twitter may still be validating your application.");
                return false;
            }
            return true;
        }

        private static void SetLocation(Location location)
        {
            Log($"[!]Setting location to {location.Latitude},{location.Longitude}");
            s_pogoSession.Player.SetCoordinates(location.Latitude, location.Longitude);
        }

        //Evaluate if a pokemon is worth tweeting about.
        private static FoundPokemon ProcessPokemon(WildPokemon pokemon, List<FoundPokemon> alreadyFound, DateTime lastTweet)
        {
            FoundPokemon foundPokemon = new FoundPokemon
            {
                Location = new Location { Latitude = pokemon.Latitude, Longitude = pokemon.Longitude },
                Kind = pokemon.PokemonData.PokemonId,
                LifeExpectancy = pokemon.TimeTillHiddenMs / 1000,
                //There is a better way to do this instead of pokemon.TimeTillHiddenMs/1000. Haven't figured it out.
                ExpirationTime = DateTime.Now.AddSeconds(pokemon.TimeTillHiddenMs / 1000).ToLocalTime()
            };

            if (s_config.ExcludedPokemon.Contains(foundPokemon.Kind))
            {
                Log($"[!]Excluded: {foundPokemon.Kind} ({foundPokemon.LifeExpectancy} seconds): {Math.Round(foundPokemon.Location.Latitude, 6)},{Math.Round(foundPokemon.Location.Longitude, 6)}");
                return null;
            }

            if (foundPokemon.LifeExpectancy < s_config.MinimumLifeExpectancy)
            {
                Log($"[!]Expiring: {foundPokemon.Kind} ({foundPokemon.LifeExpectancy} seconds): {Math.Round(foundPokemon.Location.Latitude, 6)},{Math.Round(foundPokemon.Location.Longitude, 6)}");
                return null;
            }

            if (alreadyFound.Contains(foundPokemon))
            {
                Log($"[!]Duplicate: {foundPokemon.Kind} ({foundPokemon.LifeExpectancy} seconds): {Math.Round(foundPokemon.Location.Latitude, 6)},{Math.Round(foundPokemon.Location.Longitude, 6)}");
                return null;
            }

            if ((lastTweet + TimeSpan.FromSeconds(s_config.RateLimit) > DateTime.Now) && !s_config.PriorityPokemon.Contains(foundPokemon.Kind))
            {
                Log($"[!]Limiting: {foundPokemon.Kind} ({foundPokemon.LifeExpectancy} seconds): {Math.Round(foundPokemon.Location.Latitude, 6)},{Math.Round(foundPokemon.Location.Longitude, 6)}");
                return null;
            }

            Log($"[!]Tweeting: {foundPokemon.Kind} ({foundPokemon.LifeExpectancy} seconds): {Math.Round(foundPokemon.Location.Latitude, 6)},{Math.Round(foundPokemon.Location.Longitude, 6)}");
            return foundPokemon;
        }

        //Build a tweet with useful information about the pokemon, then cram in as many hashtags as will fit.
        private static string ComposeTweet(FoundPokemon pokemon, Region region)
        {
            Log("[!]Composing Tweet");
            string latitude = pokemon.Location.Latitude.ToString(System.Globalization.CultureInfo.GetCultureInfo("en-us"));
            string longitude = pokemon.Location.Longitude.ToString(System.Globalization.CultureInfo.GetCultureInfo("en-us"));
            string mapsLink = $"https://www.google.com/maps/place/{latitude},{longitude}";
            string pokeVisionLink = $"https://pokevision.com/#/@{latitude},{longitude}";
            string expiration = DateTime.Now.AddSeconds(pokemon.LifeExpectancy).ToLocalTime().ToShortTimeString();
            string tweet = "";

            if (s_config.PriorityPokemon.Contains(pokemon.Kind))
            {
                tweet = string.Format(s_config.PriorityTweet, SpellCheckPokemon(pokemon.Kind), region.Prefix, region.Name, region.Suffix, expiration, mapsLink, pokeVisionLink);
            }
            else
            {
                tweet = string.Format(s_config.RegularTweet, SpellCheckPokemon(pokemon.Kind), region.Prefix, region.Name, region.Suffix, expiration, mapsLink, pokeVisionLink);
            }
            tweet = Regex.Replace(tweet, @"\s\s", @" ");
            tweet = Regex.Replace(tweet, @"\s[!]", @"!");

            //if (s_config.TagPokemon && (Tweet.Length(tweet + " #" + SpellCheckPokemon(pokemon.Kind, true)) < 138))
            //tweet += " #" + SpellCheckPokemon(pokemon.Kind, true);

            //if (s_config.TagRegion && (Tweet.Length(tweet + " #" + Regex.Replace(region.Name, @"\s+", "")) < 138))
            //tweet += " #" + Regex.Replace(region.Name, @"\s+", "");

            foreach (string tag in s_config.CustomTags)
            {
                if (Tweet.Length(tweet + tag) < 138)
                    tweet += " #" + tag;
            }

            Log("[!]Sucessfully composed tweet.");
            return tweet;
        }

        //Generate user friendly and hashtag friendly pokemon names
        private static string SpellCheckPokemon(PokemonId pokemon, bool isHashtag = false)
        {
            string display;
            switch (pokemon)
            {
                case PokemonId.Farfetchd:
                    display = isHashtag ? "Farfetchd" : "Farfetch'd";
                    break;
                case PokemonId.MrMime:
                    display = isHashtag ? "MrMime" : "Mr. Mime";
                    break;
                case PokemonId.NidoranFemale:
                    display = isHashtag ? "Nidoran" : "Nidoran♀";
                    break;
                case PokemonId.NidoranMale:
                    display = isHashtag ? "Nidoran" : "Nidoran♂";
                    break;
                default:
                    display = pokemon.ToString();
                    break;
            }
            if (s_config.PokemonOverrides.Any(po => po.Kind == pokemon))
            {
                display = s_config.PokemonOverrides.First(po => po.Kind == pokemon).Display;
            }
            Regex regex = new Regex("[^a-zA-Z0-9]");
            return isHashtag ? regex.Replace(display, "") : display;
        }

        private static void Log(string message)
        {
            Console.WriteLine(message);
            using (StreamWriter w = File.AppendText("log.txt"))
            {
                w.WriteLine(DateTime.Now + ": " + message);
            }
        }

        //Personal method
        private static void listLocations()
        {
            string list = "";
            string mapsLink = $"https://www.google.com/maps/place/";
            int regionIndex = s_config.Regions.Count;
            for (int i = 0; i < regionIndex; i++)
            {
                list += s_config.Regions[i].Name;
                list += "\r\n";
                int locationIndex = s_config.Regions[i].Locations.Count;
                for (int j = 0; j < locationIndex; j++)
                {
                    list += mapsLink;
                    list += s_config.Regions[i].Locations[j].Latitude;
                    list += ",";
                    list += s_config.Regions[i].Locations[j].Longitude;
                    list += "\r\n";
                }
                list += "\r\n";
            }
            System.IO.StreamWriter file = new System.IO.StreamWriter("location.txt");
            file.WriteLine(list);
            file.Close();
        }

        private static void excludedPokemonList()
        {
            string list = "";
            int excludedPokemonIndex = s_config.ExcludedPokemon.Count;
            list += "Excluded Pokemon \r\n";
            for (int i = 0; i < excludedPokemonIndex; i++)
            {
                PokemonId id = s_config.ExcludedPokemon[i];
                list += id;
                list += "\r\n";
            }
            System.IO.StreamWriter file = new System.IO.StreamWriter("excludedpokemon.txt");
            file.WriteLine(list);
            file.Close();
        }


        private static Configuration s_config;
        private static IAuthenticatedUser s_twitterClient;
        private static Session s_pogoSession;
    }
}
