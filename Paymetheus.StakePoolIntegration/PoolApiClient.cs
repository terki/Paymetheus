// Copyright(c) 2016-2017 The Decred developers
// Licensed under the ISC license.  See LICENSE file in the project root for full license information.

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Paymetheus.StakePoolIntegration
{
    public sealed class PoolApiClient
    {
        public const uint EarliestSupportedVersion = 1;
        public const uint LatestSupportedVersion = 2;

        public static bool IsSupportedApiVersion(uint apiVersion) =>
            apiVersion >= EarliestSupportedVersion && apiVersion <= LatestSupportedVersion;

        public static uint BestSupportedApiVersion(IEnumerable<uint> apiVersions) =>
            apiVersions.Where(IsSupportedApiVersion).Max();

        readonly Uri _poolUri;
        readonly uint _version;
        readonly string _versionString;
        readonly string _apiToken;
        readonly HttpClient _httpClient;
        readonly JsonSerializer _jsonSerializer = new JsonSerializer();

        Uri RequestUri(Uri poolUri, string request) => new Uri(poolUri, $"api/{_versionString}/{request}");

        HttpRequestMessage CreateApiRequest(HttpMethod httpMethod, string apiMethod)
        {
            var requestUri = RequestUri(_poolUri, apiMethod);
            var requestMessage = new HttpRequestMessage(httpMethod, requestUri);
            requestMessage.Headers.Add("Authorization", "Bearer " + _apiToken);
            return requestMessage;
        }

        async Task<T> UnmarshalContentAsync<T>(HttpContent content)
        {
            using (var stream = await content.ReadAsStreamAsync())
            using (var jsonReader = new JsonTextReader(new StreamReader(stream)))
            {
                return _jsonSerializer.Deserialize<T>(jsonReader);
            }
        }

        public PoolApiClient(uint apiVersion, Uri poolUri, string apiToken, HttpClient httpClient)
        {
            if (poolUri == null)
                throw new ArgumentNullException(nameof(poolUri));
            if (apiToken == null)
                throw new ArgumentNullException(nameof(apiToken));
            if (httpClient == null)
                throw new ArgumentNullException(nameof(httpClient));

            if (apiVersion > LatestSupportedVersion)
            {
                throw new ArgumentException("API version must not be greater than the last supported version.");
            }
            if (poolUri.Scheme != "https")
            {
                throw new ArgumentException("In order to protect API tokens, stakepools must serve API over HTTPS.");
            }

            _poolUri = poolUri;
            _version = apiVersion;
            _versionString = $"v{apiVersion}";
            _apiToken = apiToken;
            _httpClient = httpClient;
        }

        public async Task CreateVotingAddressAsync(string pubKeyAddress)
        {
            if (pubKeyAddress == null)
                throw new ArgumentNullException(nameof(pubKeyAddress));

            var request = CreateApiRequest(HttpMethod.Post, "address");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["UserPubKeyAddr"] = pubKeyAddress,
            });
            var httpResponse = await _httpClient.SendAsync(request);
            httpResponse.EnsureSuccessStatusCode();

            var apiResponse = await UnmarshalContentAsync<PoolApiResponse>(httpResponse.Content);
            apiResponse.EnsureSuccess();
        }

        public async Task SetVoteBitsAsync(ushort voteBits)
        {
            if (_version < 2)
            {
                throw new InvalidOperationException("This method is not supported by the client's API version.");
            }

            var request = CreateApiRequest(HttpMethod.Post, "voting");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["VoteBits"] = voteBits.ToString(),
            });
            var httpResponse = await _httpClient.SendAsync(request);
            httpResponse.EnsureSuccessStatusCode();

            var apiResponse = await UnmarshalContentAsync<PoolApiResponse>(httpResponse.Content);
            apiResponse.EnsureSuccess();
        }

        public async Task<PoolUserInfo> GetPurchaseInfoAsync()
        {
            var request = CreateApiRequest(HttpMethod.Get, "getpurchaseinfo");
            var httpResponse = await _httpClient.SendAsync(request);
            httpResponse.EnsureSuccessStatusCode();

            var apiResponse = await UnmarshalContentAsync<PoolApiResponse<PoolUserInfo>>(httpResponse.Content);
            apiResponse.EnsureSuccess();
            apiResponse.EnsureHasData();
            return apiResponse.Data;
        }
    }
}
