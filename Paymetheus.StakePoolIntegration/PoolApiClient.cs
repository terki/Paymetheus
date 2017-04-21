// Copyright(c) 2016-2017 The Decred developers
// Licensed under the ISC license.  See LICENSE file in the project root for full license information.

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace Paymetheus.StakePoolIntegration
{
    public sealed class PoolApiClient
    {
        public const uint Version = 2;
        const string VersionString = "v2";

        static Uri RequestUri(Uri poolUri, string request) => new Uri(poolUri, $"api/{VersionString}/{request}");

        readonly Uri _poolUri;
        readonly string _apiToken;
        readonly HttpClient _httpClient;
        readonly JsonSerializer _jsonSerializer = new JsonSerializer();

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

        public PoolApiClient(Uri poolUri, string apiToken, HttpClient httpClient)
        {
            if (poolUri == null)
                throw new ArgumentNullException(nameof(poolUri));
            if (apiToken == null)
                throw new ArgumentNullException(nameof(apiToken));
            if (httpClient == null)
                throw new ArgumentNullException(nameof(httpClient));

            if (poolUri.Scheme != "https")
            {
                throw new ArgumentException("In order to protect API tokens, stakepools must serve API over HTTPS.");
            }

            _poolUri = poolUri;
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
