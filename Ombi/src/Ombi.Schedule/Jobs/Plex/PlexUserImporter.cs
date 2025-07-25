﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ombi.Api.Plex;
using Ombi.Core.Authentication;
using Ombi.Core.Engine;
using Ombi.Core.Settings;
using Ombi.Core.Settings.Models.External;
using Ombi.Helpers;
using Ombi.Hubs;
using Ombi.Settings.Settings.Models;
using Ombi.Store.Entities;
using Quartz;

namespace Ombi.Schedule.Jobs.Plex
{
    public class PlexUserImporter : IPlexUserImporter
    {
        public PlexUserImporter(IPlexApi api, OmbiUserManager um, ILogger<PlexUserImporter> log,
            ISettingsService<PlexSettings> plexSettings, ISettingsService<UserManagementSettings> ums, INotificationHubService notificationHubService,
            IUserDeletionEngine userDeletionEngine)
        {
            _api = api;
            _userManager = um;
            _log = log;
            _plexSettings = plexSettings;
            _userManagementSettings = ums;
            _notification = notificationHubService;
            _userDeletionEngine = userDeletionEngine;
            _plexSettings.ClearCache();
            _userManagementSettings.ClearCache();
        }

        private readonly IPlexApi _api;
        private readonly OmbiUserManager _userManager;
        private readonly ILogger<PlexUserImporter> _log;
        private readonly ISettingsService<PlexSettings> _plexSettings;
        private readonly ISettingsService<UserManagementSettings> _userManagementSettings;
        private readonly INotificationHubService _notification;
        private readonly IUserDeletionEngine _userDeletionEngine;

        public async Task Execute(IJobExecutionContext job)
        {
            var userManagementSettings = await _userManagementSettings.GetSettingsAsync();
            if (!userManagementSettings.ImportPlexUsers && !userManagementSettings.ImportPlexAdmin)
            {
                return;
            }

            var settings = await _plexSettings.GetSettingsAsync();
            if (!settings.Enable)
            {
                return;
            }

            await _notification.SendNotificationToAdmins("Plex User Importer Started");
            var allUsers = await _userManager.Users.Where(x => x.UserType == UserType.PlexUser).ToListAsync();
            List<OmbiUser> newOrUpdatedUsers = new List<OmbiUser>();

            foreach (var server in settings.Servers)
            {
                if (string.IsNullOrEmpty(server.PlexAuthToken))
                {
                    continue;
                }

                
                if (userManagementSettings.ImportPlexAdmin)
                {
                    await ImportAdmin(userManagementSettings, server, allUsers);
                }
                if (userManagementSettings.ImportPlexUsers)
                {
                    newOrUpdatedUsers.AddRange(await ImportPlexUsers(userManagementSettings, allUsers, server));
                }
            }

            if (userManagementSettings.CleanupPlexUsers)
            {
                // Refresh users from updates
                allUsers = await _userManager.Users.Where(x => x.UserType == UserType.PlexUser)
                    .ToListAsync();

               var missingUsers = allUsers
                    .Where(x => !newOrUpdatedUsers.Contains(x)).ToList();

                // Don't delete any admins
                for (int i = missingUsers.Count() - 1; i >= 0; i--)
                {
                    var isAdmin = await _userManager.IsInRoleAsync(missingUsers[i], OmbiRoles.Admin);
                    if (!isAdmin)
                    {
                        continue;
                    }

                    missingUsers.RemoveAt(i);
                }
                
                foreach (var ombiUser in missingUsers)
                {
                    _log.LogInformation("Deleting user {0} not found in Plex Server.", ombiUser.UserName);
                    await _userDeletionEngine.DeleteUser(ombiUser);
                }
            }
            

            await _notification.SendNotificationToAdmins("Plex User Importer Finished");
        }

        private async Task<List<OmbiUser>> ImportPlexUsers(UserManagementSettings userManagementSettings, 
            List<OmbiUser> allUsers, PlexServers server)
        {
            var users = await _api.GetUsers(server.PlexAuthToken);

            List<OmbiUser> newOrUpdatedUsers = new List<OmbiUser>();
            
            foreach (var plexUser in users.User)
            {
                // Skip users without server access
                if (plexUser.Server == null || !plexUser.Server.Any())
                {
                    _log.LogInformation($"Skipping user {plexUser.Username ?? plexUser.Id} as they have no server access");
                    continue;
                }

                // Check if we should import this user
                if (userManagementSettings.BannedPlexUserIds.Contains(plexUser.Id))
                {
                    // Do not import these, they are not allowed into the country.
                    continue;
                }

                // Check if this Plex User already exists
                // We are using the Plex USERNAME and Not the TITLE, the Title is for HOME USERS without an account
                var existingPlexUser = allUsers.FirstOrDefault(x => x.ProviderUserId == plexUser.Id);
                if (existingPlexUser == null)
                {
                    if (!plexUser.Username.HasValue())
                    {
                        _log.LogInformation($"Could not create user since the have no username (Probably a Home User), PlexUserId: {plexUser.Id}, Title: {plexUser.Title}");
                        continue;
                    }

                    if ((plexUser.Email.HasValue()) && await _userManager.FindByEmailAsync(plexUser.Email) != null)
                    {
                        _log.LogWarning($"Cannot add user {plexUser.Username} because their email address is already in Ombi, skipping this user");
                        continue;
                    }
                    // Create this users
                    // We do not store a password against the user since they will authenticate via Plex
                    var newUser = new OmbiUser
                    {
                        UserType = UserType.PlexUser,
                        UserName = plexUser?.Username ?? plexUser.Id,
                        ProviderUserId = plexUser.Id,
                        Email = plexUser?.Email ?? string.Empty,
                        Alias = string.Empty,
                        MovieRequestLimit = userManagementSettings.MovieRequestLimit,
                        MovieRequestLimitType = userManagementSettings.MovieRequestLimitType,
                        EpisodeRequestLimit = userManagementSettings.EpisodeRequestLimit,
                        EpisodeRequestLimitType = userManagementSettings.EpisodeRequestLimitType,
                        MusicRequestLimit = userManagementSettings.MusicRequestLimit,
                        MusicRequestLimitType = userManagementSettings.MusicRequestLimitType,
                        StreamingCountry = userManagementSettings.DefaultStreamingCountry
                    };
                    _log.LogInformation("Creating Plex user {0}", newUser.UserName);
                    var result = await _userManager.CreateAsync(newUser);
                    if (!LogResult(result))
                    {
                        continue;
                    }
                    // Get the new user object to avoid any concurrency failures
                    var dbUser =
                        await _userManager.Users.FirstOrDefaultAsync(x => x.UserName == newUser.UserName);
                    if (userManagementSettings.DefaultRoles.Any())
                    {
                        foreach (var defaultRole in userManagementSettings.DefaultRoles)
                        {
                            await _userManager.AddToRoleAsync(dbUser, defaultRole);
                        }
                    }
                    newOrUpdatedUsers.Add(dbUser);
                }
                else
                {
                    newOrUpdatedUsers.Add(existingPlexUser);
                    // Do we need to update this user?
                    existingPlexUser.Email = plexUser.Email;
                    existingPlexUser.UserName = plexUser.Username;

                    await _userManager.UpdateAsync(existingPlexUser);
                }
            }

            return newOrUpdatedUsers;
        }

        private async Task<OmbiUser> ImportAdmin(UserManagementSettings settings, PlexServers server, 
            List<OmbiUser> allUsers)
        {
            var plexAdmin = (await _api.GetAccount(server.PlexAuthToken)).user;

            // Check if the admin is already in the DB
            var adminUserFromDb = allUsers.FirstOrDefault(x =>
                x.ProviderUserId.Equals(plexAdmin.id, StringComparison.CurrentCultureIgnoreCase));

            if (adminUserFromDb != null)
            {
                // Let's update the user
                adminUserFromDb.Email = plexAdmin.email;
                adminUserFromDb.UserName = plexAdmin.username;
                adminUserFromDb.ProviderUserId = plexAdmin.id;
                await _userManager.UpdateAsync(adminUserFromDb);
                return adminUserFromDb;
            }

            // Ensure we don't have a user with the same username
            var normalUsername = plexAdmin.username.ToUpperInvariant();
            if (await _userManager.Users.AnyAsync(x => x.NormalizedUserName == normalUsername))
            {
                _log.LogWarning($"Cannot add user {plexAdmin.username} because their username is already in Ombi, skipping this user");
                return null;
            }

            var newUser = new OmbiUser
            {
                UserType = UserType.PlexUser,
                UserName = plexAdmin.username ?? plexAdmin.id,
                ProviderUserId = plexAdmin.id,
                Email = plexAdmin.email ?? string.Empty,
                Alias = string.Empty,
                StreamingCountry = settings.DefaultStreamingCountry
            };

            var result = await _userManager.CreateAsync(newUser);
            if (!LogResult(result))
            {
                return null;
            }

            var roleResult = await _userManager.AddToRoleAsync(newUser, OmbiRoles.Admin);
            LogResult(roleResult);
            return newUser;
        }

        private bool LogResult(IdentityResult result)
        {
            if (!result.Succeeded)
            {
                foreach (var identityError in result.Errors)
                {
                    _log.LogError(LoggingEvents.PlexUserImporter, identityError.Description);
                }
            }
            return result.Succeeded;
        }

        private bool _disposed;
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _userManager?.Dispose();
                //_plexSettings?.Dispose();
                //_userManagementSettings?.Dispose();
            }
            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
