﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.ChangeFeed;

#nullable enable

namespace BuildXL.Cache.ContentStore.Interfaces.Auth;

/// <summary>
/// Provides Azure Storage authentication options based on a <see cref="Secret"/> (Connection string or SAS Token)
/// </summary>
public class SecretBasedAzureStorageCredentials : IAzureStorageCredentials
{
    /// <summary>
    /// Credentials for local storage emulator.
    ///
    /// Used for tests only.
    /// </summary>
    public static SecretBasedAzureStorageCredentials StorageEmulator = new SecretBasedAzureStorageCredentials(connectionString: "UseDevelopmentStorage=true");

    private readonly Secret _secret;

    /// <summary>
    /// Creates a fixed credential; this is our default mode of authentication.
    /// </summary>
    /// <remarks>
    /// This is just a convenience method that's actually equivalent to using a <see cref="PlainTextSecret"/>
    /// </remarks>
    public SecretBasedAzureStorageCredentials(string connectionString)
        : this(new PlainTextSecret(secret: connectionString))
    {
    }

    /// <nodoc />
    public SecretBasedAzureStorageCredentials(Secret secret)
    {
        _secret = secret;
    }

    private static readonly Regex s_storageAccountNameRegex = new Regex(".*;AccountName=(?<accountName>[^;]+);.*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_storageUrlRegex = new Regex("https?://(?<accountName>.+)\\.blob\\..*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <nodoc />
    public string GetAccountName()
    {
        switch (_secret)
        {
            case PlainTextSecret plainTextSecret:
                var match = s_storageAccountNameRegex.Match(plainTextSecret.Secret);
                if (match.Success)
                {
                    return match.Groups["accountName"].Value;
                }

                match = s_storageUrlRegex.Match(plainTextSecret.Secret);
                if (match.Success)
                {
                    return match.Groups["accountName"].Value;
                }

                throw new InvalidOperationException($"The provided secret is malformed and the account name could not be retrieved.");
            case UpdatingSasToken updatingSasToken:
                return updatingSasToken.Token.StorageAccount;
            default:
                throw new NotImplementedException($"Unknown secret type `{_secret.GetType()}`");
        }
    }

    #region Blob V12 API

    /// <nodoc />
    public BlobServiceClient CreateBlobServiceClient(BlobClientOptions? blobClientOptions = null)
    {
        blobClientOptions = BlobClientOptionsFactory.CreateOrOverride(blobClientOptions);

        return _secret switch
        {
            PlainTextSecret plainText => new BlobServiceClient(connectionString: plainText.Secret, blobClientOptions),
            UpdatingSasToken sasToken => new BlobServiceClient(
                serviceUri: new Uri($"https://{sasToken.Token.StorageAccount}.blob.core.windows.net/"),
                credential: CreateV12StorageCredentialsFromSasToken(sasToken),
                blobClientOptions),
            _ => throw new NotImplementedException($"Unknown secret type `{_secret.GetType()}`")
        };
    }

    /// <nodoc />
    public BlobChangeFeedClient CreateBlobChangeFeedClient(BlobClientOptions? blobClientOptions = null, BlobChangeFeedClientOptions? changeFeedClientOptions = null)
    {
        blobClientOptions = BlobClientOptionsFactory.CreateOrOverride(blobClientOptions);

        changeFeedClientOptions ??= new BlobChangeFeedClientOptions();

        return _secret switch
        {
            PlainTextSecret plainText => new BlobChangeFeedClient(connectionString: plainText.Secret, blobClientOptions, changeFeedClientOptions),
            UpdatingSasToken sasToken => new BlobChangeFeedClient(
                serviceUri: new Uri($"https://{sasToken.Token.StorageAccount}.blob.core.windows.net/"),
                credential: CreateV12StorageCredentialsFromSasToken(sasToken),
                blobClientOptions,
                changeFeedClientOptions),
            _ => throw new NotImplementedException($"Unknown secret type `{_secret.GetType()}`")
        };
    }

    /// <nodoc />
    public BlobContainerClient CreateContainerClient(string containerName, BlobClientOptions? blobClientOptions = null)
    {
        blobClientOptions = BlobClientOptionsFactory.CreateOrOverride(blobClientOptions);

        return _secret switch
        {
            PlainTextSecret plainText => new BlobContainerClient(connectionString: plainText.Secret, containerName, blobClientOptions),
            UpdatingSasToken sasToken => new BlobContainerClient(
                blobContainerUri: new Uri($"https://{sasToken.Token.StorageAccount}.blob.core.windows.net/{containerName}"),
                CreateV12StorageCredentialsFromSasToken(sasToken),
                blobClientOptions),
            _ => throw new NotImplementedException($"Unknown secret type `{_secret.GetType()}`")
        };
    }

    private AzureSasCredential CreateV12StorageCredentialsFromSasToken(UpdatingSasToken updatingSasToken)
    {
        var credential = new AzureSasCredential(updatingSasToken.Token.Token);
        updatingSasToken.TokenUpdated += (_, replacementSasToken) =>
                                         {
                                             credential.Update(replacementSasToken.Token);
                                         };

        return credential;
    }

    /// <nodoc />
    public AzureSasCredential GetContainerSasCredential(string containerName)
    {
        return _secret switch
        {
            PlainTextSecret plainText => throw new NotSupportedException("Cannot derive a SAS credential from the current secret."),
            UpdatingSasToken sasToken => CreateV12StorageCredentialsFromSasToken(sasToken),
            _ => throw new NotImplementedException($"Unknown secret type `{_secret.GetType()}`")
        };
    }

    #endregion
}
