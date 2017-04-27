// Copyright (c) 2017 The Decred developers
// Licensed under the ISC license.  See LICENSE file in the project root for full license information.

using Newtonsoft.Json;
using Paymetheus.Decred;
using Paymetheus.Framework;
using Paymetheus.StakePoolIntegration;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Paymetheus.ViewModels
{
    sealed class ManageStakePoolsDialogViewModel : DialogViewModelBase, IActivity, INotifyCompletion
    {
        HttpClient _httpClient = new HttpClient();
        JsonSerializer _jsonSerializer = new JsonSerializer();
        Dictionary<string, StakePoolUserConfig.Entry> _configuredPools = new Dictionary<string, StakePoolUserConfig.Entry>();
        string _configPath = Path.Combine(App.Current.AppDataDir, "stakepoolcfg.json");

        public ObservableCollection<StakePoolInfo> AvailablePools { get; } = new ObservableCollection<StakePoolInfo>();

        internal IEnumerable<Tuple<StakePoolInfo, StakePoolUserConfig.Entry>> ConfiguredPools => AvailablePools
            .Where(p => _configuredPools.ContainsKey(p.Uri.Host))
            .Select(p => Tuple.Create(p, _configuredPools[p.Uri.Host]));

        public ManageStakePoolsDialogViewModel(ShellViewModelBase shell) : base(shell)
        {
            var synchronizer = ViewModelLocator.SynchronizerViewModel as SynchronizerViewModel;
            if (synchronizer != null)
            {
                SelectedVotingAccount = synchronizer.Accounts[0];
            }

            _saveCommand = new DelegateCommandAsync(SaveCommandActionAsync);
            _saveCommand.Executable = false;
        }

        public async Task RunActivityAsync()
        {
            var poolListing = await PoolListApi.QueryStakePoolInfoAsync(_httpClient, _jsonSerializer);
            var availablePools = poolListing
                .Select(p => p.Value)
                .Where(p => p.ApiEnabled)
                .Where(p => p.Uri.Scheme == "https")
                .Where(p => p.Network == App.Current.ActiveNetwork.Name)
                .Where(p => p.SupportedApiVersions.Where(PoolApiClient.IsSupportedApiVersion).Any())
                .OrderBy(p => p.Uri.Host);

            var selectedStakePool = availablePools.FirstOrDefault();

            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var pool in availablePools)
                {
                    AvailablePools.Add(pool);
                }
                SelectedStakePool = selectedStakePool;
            });

            if (File.Exists(_configPath))
            {
                var config = await ReadConfig(_configPath);
                foreach (var entry in config.Entries)
                {
                    _configuredPools[entry.Host] = entry;
                    if (selectedStakePool != null && entry.Host == selectedStakePool.Uri.Host)
                    {
                        await App.Current.Dispatcher.InvokeAsync(() =>
                        {
                            SelectedConfiguredPool = entry;
                            SelectedPoolApiKey = entry.ApiKey;
                        });
                    }
                }
            }
        }

        public TaskCompletionSource<object> NotifyCompletionSource { get; } = new TaskCompletionSource<object>();

        async Task<StakePoolUserConfig> ReadConfig(string configPath)
        {
            using (var sr = new StreamReader(configPath, Encoding.UTF8))
            {
                return await StakePoolUserConfig.ReadConfig(_jsonSerializer, sr);
            }
        }

        async Task WriteConfig(StakePoolUserConfig config, string configPath)
        {
            using (var sw = new StreamWriter(configPath, false, Encoding.UTF8))
            {
                await config.WriteConfig(_jsonSerializer, sw);
            }
        }

        private StakePoolInfo _selectedStakePool = null;
        public StakePoolInfo SelectedStakePool
        {
            get { return _selectedStakePool; }
            set
            {
                _selectedStakePool = value;
                RaisePropertyChanged();

                if (value == null)
                {
                    SelectedConfiguredPool = null;
                    SelectedPoolApiKey = "";
                    return;
                }

                StakePoolUserConfig.Entry configEntry;
                _configuredPools.TryGetValue(value.Uri.Host, out configEntry);
                SelectedConfiguredPool = configEntry;
                SelectedPoolApiKey = configEntry?.ApiKey ?? "";
            }
        }

        private string _selectedPoolApiKey = "";
        public string SelectedPoolApiKey
        {
            get { return _selectedPoolApiKey; }
            set { _selectedPoolApiKey = value; RaisePropertyChanged(); }
        }

        private StakePoolUserConfig.Entry _selectedConfiguredPool;
        public StakePoolUserConfig.Entry SelectedConfiguredPool
        {
            get { return _selectedConfiguredPool; }
            set
            {
                _selectedConfiguredPool = value;
                RaisePropertyChanged();

                _saveCommand.Executable = value == null;
                NeedsSaving = value == null;

                if (value != null)
                {
                    var synchronizer = ViewModelLocator.SynchronizerViewModel as SynchronizerViewModel;
                    if (synchronizer != null)
                    {
                        SelectedVotingAccount = synchronizer.Accounts.First(a => a.AccountNumber == value.VotingAccount);
                    }
                }
            }
        }

        private AccountViewModel _selectedVotingAccount;
        public AccountViewModel SelectedVotingAccount
        {
            get { return _selectedVotingAccount; }
            set { _selectedVotingAccount = value; RaisePropertyChanged(); }
        }

        private bool _needsSaving = false;
        public bool NeedsSaving
        {
            get { return _needsSaving; }
            set { _needsSaving = value; RaisePropertyChanged(); }
        }

        private DelegateCommandAsync _saveCommand;
        public ICommand SaveCommand => _saveCommand;

        private async Task SaveCommandActionAsync()
        {
            try
            {
                var nextAddressResponse = await App.Current.Synchronizer.WalletRpcClient.NextExternalAddressAsync(SelectedVotingAccount.Account);
                var pubKeyAddress = nextAddressResponse.Item2;

                var bestApiVersion = PoolApiClient.BestSupportedApiVersion(SelectedStakePool.SupportedApiVersions);
                var client = new PoolApiClient(bestApiVersion, SelectedStakePool.Uri, SelectedPoolApiKey, _httpClient);

                // Sumbit the pubkey.  In the current stakepool api version only a single pubkey can
                // be submitted per user, so if one was already submitted, simply ignore the error.
                try
                {
                    await client.CreateVotingAddressAsync(pubKeyAddress);
                }
                catch (PoolApiResponseException ex) when (ex.Code == StatusCode.AlreadyExists) { }

                var poolInfo = await client.GetPurchaseInfoAsync();
                var configEntry = new StakePoolUserConfig.Entry
                {
                    ApiKey = SelectedPoolApiKey,
                    Host = SelectedStakePool.Uri.Host,
                    MultisigVoteScript = poolInfo.RedeemScriptHex,
                    VotingAccount = SelectedVotingAccount.Account.AccountNumber,
                };
                _configuredPools[configEntry.Host] = configEntry;

                var config = new StakePoolUserConfig { Entries = _configuredPools.Values.ToArray() };
                await WriteConfig(config, _configPath);

                SelectedConfiguredPool = configEntry;

                _saveCommand.Executable = false;
                NeedsSaving = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error communicating with stake pool");
            }
        }
    }
}
