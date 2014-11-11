﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using AdaptiveSystems.AspNetIdentity.AzureTableStorage.Exceptions;
using Microsoft.AspNet.Identity;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using NExtensions;

namespace AdaptiveSystems.AspNetIdentity.AzureTableStorage
{
    public class UserStore<T> : IUserStore<T>, IUserPasswordStore<T>, IUserEmailStore<T>, IUserLockoutStore<T, string>, IUserLoginStore<T>, IDisposable 
                        where T : User, new()
    {
        private readonly IdentityTables identityTables;

        public UserStore(string connectionString) : this(CloudStorageAccount.Parse(connectionString)) { }
        public UserStore(CloudStorageAccount storageAccount) : this(storageAccount, true) { }
        public UserStore(CloudStorageAccount storageAccount, bool createIfNotExists) : this(storageAccount, createIfNotExists, "users", "userNamesIndex", "userEmailsIndex", "userExternalLoginsIndex") { }
        public UserStore(CloudStorageAccount storageAccount, bool createIfNotExists, string usersTableName, string userNamesIndexTableName, string userEmailsIndexTableName, string userExternalLoginsIndexTableName)
        {
            identityTables = new IdentityTables(storageAccount, createIfNotExists, usersTableName, userNamesIndexTableName, userEmailsIndexTableName, userExternalLoginsIndexTableName);
        }

        public static UserStore<T> Create()
        {
            return new UserStore<T>(ConfigurationManager.ConnectionStrings["UserStore.ConnectionString"].ConnectionString);
        }

        private async Task CreateUserNameIndex(T user)
        {
            var userNameIndex = new UserNameIndex(user.UserName.Base64Encode(), user.Id);

            try
            {
                await identityTables.InsertUserNamesIndexTableEntity(userNameIndex);
            }
            catch (StorageException ex)
            {
                if (ex.RequestInformation.HttpStatusCode == 409)
                {
                    throw new DuplicateUsernameException();
                }
                throw;
            }
        }

        private async Task CreateEmailIndex(T user)
        {
            var emailIndex = new UserEmailIndex(user.Email.Base64Encode(), user.Id);

            try
            {
                await identityTables.InsertUserEmailsIndexTableEntity(emailIndex);
            }
            catch (StorageException ex)
            {
                if (ex.RequestInformation.HttpStatusCode == 409)
                {
                    throw new DuplicateEmailException();
                }
                throw;
            }
        }

        public async Task CreateAsync(T user)
        {
            user.ThrowIfNull("user");
            user.SetPartionAndRowKeys();

            await CreateUserNameIndex(user);
            await CreateEmailIndex(user);

            try
            {
                await identityTables.InsertOrReplaceUserTableEntity(user);
            }
            catch (Exception)
            {
                // attempt to delete the index item - needs work
                RemoveIndices(user).Wait();//cannt await in the catch of a try block so have to wait
                throw;
            }
        }

        public async Task DeleteAsync(T user)
        {
            user.ThrowIfNull("user");

            await identityTables.DeleteUserTableEntity(user);

            await RemoveIndices(user);
        }

        public async Task<T> FindByIdAsync(string userId) 
        {
            userId.ThrowIfNullOrEmpty("userId");

            var query = new TableQuery<T>()
                        .Where(TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, userId))
                        .Take(1);
            var results = await identityTables.ExecuteQueryOnUser(query);
            return results.SingleOrDefault();
        }

        public async Task<T> FindByNameAsync(string userName)
        {
            userName.ThrowIfNullOrEmpty("userName");

            var indexQuery = new TableQuery<UserNameIndex>()
                            .Where(TableQuery.GenerateFilterCondition("PartitionKey", 
                                    QueryComparisons.Equal, userName.Base64Encode()))
                            .Take(1);
            var indexResults = await identityTables.ExecuteQueryOnUserNamesIndex(indexQuery);
            var indexItem = indexResults.SingleOrDefault();

            if (indexItem == null)
            {
                return null;
            }

            return FindByIdAsync(indexItem.UserId).Result;
        }

        public async Task UpdateAsync(T user)
        {
            user.ThrowIfNull("user");

            await identityTables.UpdateUserTableEntity(user);
        }

        public void Dispose()
        {
            
        }

        public Task<string> GetPasswordHashAsync(T user)
        {
            user.ThrowIfNull("user");

            return Task.FromResult(user.PasswordHash);
        }

        public Task<bool> HasPasswordAsync(T user)
        {
            user.ThrowIfNull("user");

            return Task.FromResult(user.PasswordHash.HasValue());
        }

        public Task SetPasswordHashAsync(T user, string passwordHash)
        {
            user.ThrowIfNull("user");
            passwordHash.ThrowIfNullOrEmpty("passwordHash");

            user.PasswordHash = passwordHash;
            return Task.FromResult(0);
        }

        public async Task<T> FindByEmailAsync(string email)
        {
            email.ThrowIfNullOrEmpty("email");

            var indexQuery = new TableQuery<UserEmailIndex>()
                            .Where(TableQuery.GenerateFilterCondition("PartitionKey",
                                    QueryComparisons.Equal, email.Base64Encode()))
                            .Take(1);
            var indexResults = await identityTables.ExecuteQueryOnUserEmailsIndex(indexQuery);
            var indexItem = indexResults.SingleOrDefault();

            return indexItem == null ? null : FindByIdAsync(indexItem.UserId).Result;
        }

        public Task<string> GetEmailAsync(T user)
        {
            user.ThrowIfNull("user");

            return Task.FromResult(user.Email);
        }

        public Task<bool> GetEmailConfirmedAsync(T user)
        {
            user.ThrowIfNull("user");

            return Task.FromResult(user.EmailConfirmed);
        }

        public Task SetEmailAsync(T user, string email)
        {
            user.ThrowIfNull("user");
            email.ThrowIfNullOrEmpty("email");

            user.Email = email;
            return Task.FromResult(0);
        }

        public Task SetEmailConfirmedAsync(T user, bool confirmed)
        {
            user.ThrowIfNull("user");

            user.EmailConfirmed = confirmed;
            return Task.FromResult(0);
        }

        private async Task RemoveIndices(T user)
        {
            var userNameIndex = new UserNameIndex(user.UserName.Base64Encode(), user.Id);

            var emailIndex = new UserEmailIndex(user.Email.Base64Encode(), user.Id);

            var t1 = identityTables.DeleteUserNamesIndexTableEntity(userNameIndex);
            var t2 = identityTables.DeleteUserEmailsIndexTableEntity(emailIndex);

            await Task.WhenAll(t1, t2);
        }


        public Task<int> GetAccessFailedCountAsync(T user)
        {
            throw new NotImplementedException();
        }

        public Task<bool> GetLockoutEnabledAsync(T user)
        {
            user.ThrowIfNull("user");
            return Task.FromResult(user.LockoutEnabled);
        }

        public Task<DateTimeOffset> GetLockoutEndDateAsync(T user)
        {
            user.ThrowIfNull("user");
            return Task.FromResult((DateTimeOffset)DateTime.SpecifyKind(user.LockoutEndDate ?? new DateTime(1601, 1, 1), DateTimeKind.Utc));
        }

        public Task<int> IncrementAccessFailedCountAsync(T user)
        {
            user.ThrowIfNull("user");
            user.AccessFailedCount++;
            return Task.FromResult(0);
        }

        public Task ResetAccessFailedCountAsync(T user)
        {
            user.ThrowIfNull("user");
            user.AccessFailedCount = 0;
            return Task.FromResult(0);
        }

        public Task SetLockoutEnabledAsync(T user, bool enabled)
        {
            user.ThrowIfNull("user");
            user.LockoutEnabled = enabled;
            return Task.FromResult(0);
        }

        public Task SetLockoutEndDateAsync(T user, DateTimeOffset lockoutEnd)
        {
            user.ThrowIfNull("user");

            user.LockoutEndDate = lockoutEnd.UtcDateTime;
            return Task.FromResult(0);
        }

        public async Task AddLoginAsync(T user, UserLoginInfo login)
        {
            user.ThrowIfNull("user");
            login.ThrowIfNull("login");

            user.AddExternalLogin(login);
            await UpdateAsync(user);
            await CreateExternalLoginIndex(user, login);
        }

        public async Task RemoveLoginAsync(T user, UserLoginInfo login)
        {
            user.ThrowIfNull("user");
            login.ThrowIfNull("login");

            user.RemoveExternalLogin(login);
            await UpdateAsync(user);
            await identityTables.DeleteUserExternalLoginIndexTableEntity(new UserExternalLoginIndex(login));
        }

        public Task<IList<UserLoginInfo>> GetLoginsAsync(T user)
        {
            user.ThrowIfNull("user");

            return Task.FromResult((IList<UserLoginInfo>)user.GetExternalLogins());
        }

        public async Task<T> FindAsync(UserLoginInfo login)
        {
            login.ThrowIfNull("login");

            var indexItem = new UserExternalLoginIndex(login);
            var index = await identityTables.RetrieveUserExternalLoginIndexAsync(indexItem);

            return index == null 
                ? null 
                : await FindByIdAsync(index.UserId);
        }

        private async Task CreateExternalLoginIndex(T user, UserLoginInfo login)
        {
            var loginIndex = new UserExternalLoginIndex(login, user.Id);

            try
            {
                await identityTables.InsertUserExternalLoginIndexTableEntity(loginIndex);
            }
            catch (StorageException ex)
            {
                if (ex.RequestInformation.HttpStatusCode == 409)
                {
                    throw new DuplicateExternalLoginException();
                }
                throw;
            }
        }

    }
}
