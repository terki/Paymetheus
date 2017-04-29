// Copyright (c) 2016-2017 The Decred developers
// Licensed under the ISC license.  See LICENSE file in the project root for full license information.

using Paymetheus.Decred;
using Paymetheus.Decred.Wallet;
using Paymetheus.Framework;
using Paymetheus.StakePoolIntegration;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using System.IO;
using System.Text;
using Paymetheus.Decred.Util;

namespace Paymetheus.ViewModels
{
    public interface IStakePoolSelection
    {
        string DisplayName { get; }
    }

    public class NoStakePool : IStakePoolSelection
    {
        public string DisplayName => "None";
    }

    public class ManualStakePool : IStakePoolSelection
    {
        public string DisplayName => "Manual entry";
    }

    public class StakePoolSelection : IStakePoolSelection
    {
        public StakePoolInfo PoolInfo { get; }
        public string ApiToken { get; }
        public string DisplayName => PoolInfo.Uri.Host;
        public byte[] MultisigVoteScript { get; }

        public StakePoolSelection(StakePoolInfo poolInfo, string apiToken, byte[] multisigVoteScript)
        {
            if (poolInfo == null) throw new ArgumentNullException(nameof(poolInfo));
            if (apiToken == null) throw new ArgumentNullException(nameof(apiToken));
            if (multisigVoteScript == null) throw new ArgumentNullException(nameof(multisigVoteScript));

            PoolInfo = poolInfo;
            ApiToken = apiToken;
            MultisigVoteScript = multisigVoteScript;
        }
    }

    class PurchaseTicketsViewModel : ViewModelBase, IActivity
    {
        private readonly HttpClient _httpClient = new HttpClient();
        private JsonSerializer _jsonSerializer = new JsonSerializer();
        string _configPath = Path.Combine(App.Current.AppDataDir, "stakepoolcfg.json");

        public List<StakePoolInfo> AvailablePools { get; } = new List<StakePoolInfo>();

        public PurchaseTicketsViewModel() : base()
        {
            // TODO move this
            Task.Run(async () =>
            {
                var votePrefs = await FetchWalletVotingPreferences();
                VoteVersion = votePrefs.Item1;
                AgendaChoices = votePrefs.Item2;
            }).Wait();

            var synchronizer = ViewModelLocator.SynchronizerViewModel as SynchronizerViewModel;
            if (synchronizer != null)
            {
                SelectedSourceAccount = synchronizer.Accounts[0];
            }

            ConfiguredStakePools = new ObservableCollection<IStakePoolSelection>(new List<IStakePoolSelection>
            {
                new NoStakePool(),
                new ManualStakePool(),
            });
            _selectedStakePool = ConfiguredStakePools[0];

            ManageStakePools = new DelegateCommandAsync(ManageStakePoolsActionAsync);
            ManageStakePools.Executable = false; // Set true after pool listing is downloaded and configs are read.

            _purchaseTickets = new DelegateCommand(PurchaseTicketsAction);
            _purchaseTickets.Executable = false;
        }

        public async Task RunActivityAsync()
        {
            var poolListing = await PoolListApi.QueryStakePoolInfoAsync(_httpClient, _jsonSerializer);
            AvailablePools.AddRange(poolListing
                .Select(p => p.Value)
                .Where(p => p.ApiEnabled)
                .Where(p => p.Uri.Scheme == "https")
                .Where(p => p.Network == App.Current.ActiveNetwork.Name)
                .Where(p => p.SupportedApiVersions.Where(PoolApiClient.IsSupportedApiVersion).Any())
                .OrderBy(p => p.Uri.Host));

            if (File.Exists(_configPath))
            {
                var config = await ReadConfig(_configPath);
                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var entry in config.Entries)
                    {
                        var entryInfo = AvailablePools.Where(p => p.Uri.Host == entry.Host).FirstOrDefault();
                        if (entryInfo == null)
                        {
                            continue;
                        }
                        var stakePoolSelection = new StakePoolSelection(entryInfo, entry.ApiKey, Hexadecimal.Decode(entry.MultisigVoteScript));
                        ConfiguredStakePools.Add(stakePoolSelection);
                        RaisePropertyChanged(nameof(VotePreferencesVisibility));

                        // If only one pool is saved, use this as the default.
                        if (config.Entries.Length == 1)
                        {
                            SelectedStakePool = stakePoolSelection;
                        }
                    }
                });
            }

            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                ManageStakePools.Executable = true;
                CommandManager.InvalidateRequerySuggested();
            });
        }

        async Task<StakePoolUserConfig> ReadConfig(string configPath)
        {
            using (var sr = new StreamReader(configPath, Encoding.UTF8))
            {
                return await StakePoolUserConfig.ReadConfig(_jsonSerializer, sr);
            }
        }

        private AccountViewModel _selectedSourceAccount;
        public AccountViewModel SelectedSourceAccount
        {
            get { return _selectedSourceAccount; }
            set { _selectedSourceAccount = value; RaisePropertyChanged(); }
        }

        public DelegateCommandAsync ManageStakePools { get; }
        private async Task ManageStakePoolsActionAsync()
        {
            var prevConfiguredStakepoolCount = ConfiguredStakePools.Count;

            // Open dialog that downloads stakepool listing and lets user enter their api key.
            var shell = (ShellViewModel)ViewModelLocator.ShellViewModel;
            var dialog = new ManageStakePoolsDialogViewModel(shell);
            shell.ShowDialog(dialog);
            await dialog.NotifyCompletionSource.Task; // Wait until dialog is hidden

            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var configuredPool in dialog.ConfiguredPools)
                {
                    var poolInfo = configuredPool.Item1;
                    var poolUserConfig = configuredPool.Item2;

                    if (!ConfiguredStakePools.OfType<StakePoolSelection>().Where(p => p.PoolInfo.Uri.Host == poolInfo.Uri.Host).Any())
                    {
                        var stakePoolSelection = new StakePoolSelection(poolInfo, poolUserConfig.ApiKey,
                            Hexadecimal.Decode(poolUserConfig.MultisigVoteScript));
                        ConfiguredStakePools.Add(stakePoolSelection);
                        RaisePropertyChanged(nameof(VotePreferencesVisibility));
                        SelectedStakePool = stakePoolSelection;
                    }
                }
            });

            if (prevConfiguredStakepoolCount != ConfiguredStakePools.Count)
            {
                await UpdateStakepoolVotePreferences();
            }
        }

        public ObservableCollection<IStakePoolSelection> ConfiguredStakePools { get; }

        private Visibility _votingAddressOptionVisibility = Visibility.Visible;
        public Visibility VotingAddressOptionVisibility
        {
            get { return _votingAddressOptionVisibility; }
            set { _votingAddressOptionVisibility = value; RaisePropertyChanged(); }
        }

        private Visibility _manualPoolOptionsVisibility = Visibility.Collapsed;
        public Visibility ManualPoolOptionsVisibility
        {
            get { return _manualPoolOptionsVisibility; }
            set { _manualPoolOptionsVisibility = value; RaisePropertyChanged(); }
        }

        private IStakePoolSelection _selectedStakePool;
        public IStakePoolSelection SelectedStakePool
        {
            get { return _selectedStakePool; }
            set
            {
                _selectedStakePool = value;
                RaisePropertyChanged();

                if (value is NoStakePool)
                {
                    VotingAddressOptionVisibility = Visibility.Visible;
                    ManualPoolOptionsVisibility = Visibility.Collapsed;
                }
                else if (value is ManualStakePool)
                {
                    VotingAddressOptionVisibility = Visibility.Visible;
                    ManualPoolOptionsVisibility = Visibility.Visible;
                }
                else if (value is StakePoolSelection)
                {
                    VotingAddressOptionVisibility = Visibility.Collapsed;
                    ManualPoolOptionsVisibility = Visibility.Collapsed;
                }

                EnableOrDisableSendCommand();
            }
        }

        private uint _ticketsToPurchase = 1;
        public uint TicketsToPurchase
        {
            get { return _ticketsToPurchase; }
            set { _ticketsToPurchase = value; EnableOrDisableSendCommand(); }
        }

        private const long minFeePerKb = (long)1e5;
        private const long maxFeePerKb = (long)1e8 - 1;

        private Amount _ticketFee = TransactionFees.DefaultFeePerKb;
        public string TicketFee
        {
            get { return _ticketFee.ToString(); }
            set
            {
                try
                {
                    var ticketFee = Denomination.Decred.AmountFromString(value);

                    if (ticketFee < minFeePerKb)
                        throw new ArgumentException($"Too small fee passed (must be >= {(Amount)minFeePerKb} DCR/kB)");
                    if (ticketFee > maxFeePerKb)
                        throw new ArgumentException($"Too big fee passed (must be <= {(Amount)minFeePerKb} DCR/kB)");

                    _ticketFee = ticketFee;
                }
                finally
                {
                    EnableOrDisableSendCommand();
                }
            }
        }


        private Amount _splitFee = TransactionFees.DefaultFeePerKb;
        public string SplitFee
        {
            get { return _splitFee.ToString(); }
            set
            {
                try
                {
                    var splitFee = Denomination.Decred.AmountFromString(value);

                    if (splitFee < minFeePerKb)
                        throw new ArgumentException($"Too small fee passed (must be >= {(Amount)minFeePerKb} DCR/kB)");
                    if (splitFee > maxFeePerKb)
                        throw new ArgumentException($"Too big fee passed (must be <= {(Amount)minFeePerKb} DCR/kB)");

                    _splitFee = splitFee;
                }
                finally
                {
                    EnableOrDisableSendCommand();
                }
            }
        }

        private const uint MinExpiry = 2;
        private uint _expiry = 16; // The default expiry is 16.
        public uint Expiry
        {
            get { return _expiry; }
            set
            {
                try
                {
                    if (value < MinExpiry)
                        throw new ArgumentException($"Expiry must be a minimum of {MinExpiry} blocks");

                    _expiry = value;
                }
                finally
                {
                    EnableOrDisableSendCommand();
                }
            }
        }

        // manual
        private Address _votingAddress;
        public string VotingAddress
        {
            get { return _votingAddress?.Encode() ?? ""; }
            set
            {
                try
                {
                    _votingAddress = Address.Decode(value);
                }
                catch
                {
                    _votingAddress = null;
                }
                finally
                {
                    EnableOrDisableSendCommand();
                }
            }
        }

        // manual
        private Address _poolFeeAddress;
        public string PoolFeeAddress
        {
            get { return _poolFeeAddress?.Encode() ?? ""; }
            set
            {
                try
                {
                    _poolFeeAddress = Address.Decode(value);
                }
                catch
                {
                    _poolFeeAddress = null;
                }
                finally
                {
                    EnableOrDisableSendCommand();
                }
            }
        }

        // manual
        private decimal _poolFees = 0.0m; // Percentage between 0-100 for display
        public decimal PoolFees
        {
            get { return _poolFees; }
            set
            {
                try
                {
                    _poolFees = value;
                    if (value * 100m != Math.Floor(value * 100m))
                        throw new ArgumentException("pool fees must have two decimal points of precision maximum");
                    if (value > 100.0m)
                        throw new ArgumentException("pool fees must be less or equal too than 100.00%");
                    if (value < 0.01m)
                        throw new ArgumentException("pool fees must be greater than or equal to 0.01%");
                }
                finally
                {
                    EnableOrDisableSendCommand();
                }
            }
        }

        private void EnableOrDisableSendCommand()
        {
            if (_selectedSourceAccount == null)
            {
                _purchaseTickets.Executable = false;
                return;
            }

            if (_expiry < MinExpiry)
            {
                _purchaseTickets.Executable = false;
                return;
            }

            if (_ticketsToPurchase <= 0)
            {
                _purchaseTickets.Executable = false;
                return;
            }

            if (SelectedStakePool is ManualStakePool &&
                (_poolFeeAddress == null || _poolFees * 100m != Math.Floor(_poolFees * 100m) || _poolFees < 0.01m || _poolFees > 100m))
            {
                _purchaseTickets.Executable = false;
                return;
            }

            if (!(SelectedStakePool is StakePoolSelection) && _votingAddress == null)
            {
                _purchaseTickets.Executable = false;
                return;
            }

            // Not enough funds.
            //if ((_stakeDifficultyProperties.NextTicketPrice * (Amount)_ticketsToPurchase) > _selectedSourceAccount.Balances.SpendableBalance)
            //{
            //    // TODO: Better inform the user somehow of why it doesn't allow ticket 
            //    // purchase?
            //    //
            //    // string errorString = "Not enough funds; have " +
            //    //     _selectedAccount.Balances.SpendableBalance.ToString() + " want " +
            //    //     ((Amount)(_stakeDifficultyProperties.NextTicketPrice * (Amount)_ticketsToPurchase)).ToString();
            //    // MessageBox.Show(errorString);
            //    _purchaseTickets.Executable = false;
            //    return
            //}

            _purchaseTickets.Executable = true;
        }

        private DelegateCommand _purchaseTickets;
        public ICommand Execute => _purchaseTickets;

        private void PurchaseTicketsAction()
        {
            var shell = ViewModelLocator.ShellViewModel as ShellViewModel;
            if (shell != null)
            {
                Func<string, Task<bool>> action =
                    passphrase => PurchaseTicketsWithPassphrase(passphrase);
                shell.VisibleDialogContent = new PassphraseDialogViewModel(shell,
                    "Enter passphrase to purchase tickets",
                    "PURCHASE",
                    action);
            }
        }

        private string _responseString = "";
        public string ResponseString
        {
            get { return _responseString; }
            set { _responseString = value; RaisePropertyChanged(); }
        }

        private async Task<bool> PurchaseTicketsWithPassphrase(string passphrase)
        {
            var synchronizer = App.Current.Synchronizer;
            var walletClient = synchronizer.WalletRpcClient;

            var account = SelectedSourceAccount.Account;
            var spendLimit = synchronizer.TicketPrice;
            int requiredConfirms = 2; // TODO allow user to set
            uint expiryHeight = _expiry + (uint)synchronizer.SyncedBlockHeight;

            Amount splitFeeLocal = _splitFee;
            Amount ticketFeeLocal = _ticketFee;

            Address votingAddress;
            Address poolFeeAddress;
            decimal poolFees;
            if (SelectedStakePool is StakePoolSelection)
            {
                var selection = (StakePoolSelection)SelectedStakePool;
                var bestApiVersion = PoolApiClient.BestSupportedApiVersion(selection.PoolInfo.SupportedApiVersions);
                var client = new PoolApiClient(bestApiVersion, selection.PoolInfo.Uri, selection.ApiToken, _httpClient);
                var purchaseInfo = await client.GetPurchaseInfoAsync();

                // Import the 1-of-2 multisig vote script.  This has to be done here rather than from
                // the pool management dialog since importing requires an unlocked wallet and we are
                // unable to open nested dialog windows to prompt for a passphrase.
                //
                // This does not need to re-import the script every time ticktes are purchased using
                // a pool, but for code simplicity it is done this way.  Also, in future versions of the
                // API when it may be possible to generate a new reward address for each ticket, we will
                // need to import these scripts ever time.
                await walletClient.ImportScriptAsync(selection.MultisigVoteScript, false, 0, passphrase);

                votingAddress = Address.Decode(purchaseInfo.VotingAddress);
                poolFeeAddress = Address.Decode(purchaseInfo.FeeAddress);
                poolFees = purchaseInfo.Fee;
            }
            else
            {
                votingAddress = _votingAddress;
                poolFeeAddress = _poolFeeAddress;
                poolFees = _poolFees / 100m;
            }

            List<Blake256Hash> purchaseResponse;
            try
            {
                purchaseResponse = await walletClient.PurchaseTicketsAsync(account, spendLimit,
                    requiredConfirms, votingAddress, _ticketsToPurchase, poolFeeAddress,
                    poolFees, expiryHeight, _splitFee, _ticketFee, passphrase);
            }
            catch (Grpc.Core.RpcException ex)
            {
                MessageBox.Show(ex.Status.Detail, "Unexpected error");
                return false;
            }

            ResponseString = "Success! Ticket hashes:\n" + string.Join("\n", purchaseResponse);
            return true;
        }

        public class AgendaChoiceViewModel : ViewModelBase
        {
            readonly Action<Agenda, Agenda.Choice> _setChoice;
            Agenda.Choice _selectedChoice;

            public AgendaChoiceViewModel(Agenda agenda, Agenda.Choice choice, Action<Agenda, Agenda.Choice> setChoice)
            {
                Agenda = agenda;
                _setChoice = setChoice;
                _selectedChoice = choice;
            }

            public Agenda Agenda { get; }
            public string AgendaID => Agenda.ID;
            public string AgendaDescription => Agenda.Description;
            public Agenda.Choice[] Choices => Agenda.Choices;

            public Agenda.Choice SelectedChoice
            {
                get { return _selectedChoice; }
                set
                {
                    _selectedChoice = value;
                    _setChoice(Agenda, value);
                }
            }
        }

        public Visibility VotePreferencesVisibility =>
            AgendaChoices.Any() && ConfiguredStakePools.OfType<StakePoolSelection>()
                .Where(p => p.PoolInfo.SupportedApiVersions.Where(v => v >= 2).Any()).Any()
            ? Visibility.Visible : Visibility.Collapsed;
        public uint VoteVersion { get; private set; }
        public List<AgendaChoiceViewModel> AgendaChoices { get; private set; }

        private async Task<TupleValue<uint, List<AgendaChoiceViewModel>>> FetchWalletVotingPreferences()
        {
            var walletClient = App.Current.Synchronizer.WalletRpcClient;
            var agendasTask = walletClient.AgendasAsync();
            var choicesTask = walletClient.VoteChoicesAsync();
            var agendas = await agendasTask;
            var choices = await choicesTask;
            var agendaChoices = agendas.Item2.Select(a =>
            {
                var selectedAgendaChoiceID = choices.First(c => c.Item1 == a.ID).Item2;
                return new AgendaChoiceViewModel(a, a.Choices.First(c => c.ID == selectedAgendaChoiceID), OnAgendaChoiceChanged);
            }).ToList();
            return TupleValue.Create(agendas.Item1, agendaChoices);
        }

        private void OnAgendaChoiceChanged(Agenda agenda, Agenda.Choice choice)
        {
            Task.WhenAll(SaveWalletVotePreference(agenda, choice), UpdateStakepoolVotePreferences()).ContinueWith(t =>
            {
                var ex = t.Exception;
                if (ex != null)
                {
                    MessageBox.Show(ex.InnerException.Message, "Failed to set vote preferences");
                }
            });
        }

        private Task SaveWalletVotePreference(Agenda agenda, Agenda.Choice choice)
        {
            var walletClient = App.Current.Synchronizer.WalletRpcClient;
            var choices = new TupleValue<string, string>[] { TupleValue.Create(agenda.ID, choice.ID) };
            return walletClient.SetVoteChoicesAsync(choices);
        }

        private Task UpdateStakepoolVotePreferences()
        {
            var voteBits = CalculateVoteBits();
            var updateTasks = ConfiguredStakePools.OfType<StakePoolSelection>()
                .Select(sp =>
                {
                    var bestApiVersion = PoolApiClient.BestSupportedApiVersion(sp.PoolInfo.SupportedApiVersions);
                    return TupleValue.Create(sp, bestApiVersion);
                })
                .Where(t => t.Item2 >= 2)
                .Select(t =>
                {
                    var sp = t.Item1;
                    var bestApiVersion = t.Item2;
                    var client = new PoolApiClient(bestApiVersion, sp.PoolInfo.Uri, sp.ApiToken, _httpClient);
                    return client.SetVoteBitsAsync(voteBits);
                });
            return Task.WhenAll(updateTasks);
        }

        private ushort CalculateVoteBits()
        {
            ushort voteBits = 1;
            foreach (var agendaChoice in AgendaChoices)
            {
                voteBits |= agendaChoice.SelectedChoice.Bits; // Set bits for the selected choice
            }
            return voteBits;
        }
    }
}
