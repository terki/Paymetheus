// Copyright (c) 2016 The btcsuite developers
// Copyright (c) 2016 The Decred developers
// Licensed under the ISC license.  See LICENSE file in the project root for full license information.

using Grpc.Core;
using IniParser;
using IniParser.Model;
using IniParser.Parser;
using Paymetheus.Decred;
using Paymetheus.Decred.Util;
using Paymetheus.Decred.Wallet;
using Paymetheus.Framework;
using Paymetheus.Rpc;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace Paymetheus.ViewModels
{
    public sealed class StartupWizard : WizardViewModelBase
    {
        public StartupWizard(ShellViewModelBase shell, ConsensusServerRpcOptions csro = null) : base()
        {
            CurrentDialog = new ConsensusServerRpcConnectionDialog(this, csro);
        }

        public void OnFinished()
        {
            App.Current.MarkWalletLoaded();
            Messenger.MessageSingleton<SynchronizerViewModel>(new StartupWizardFinishedMessage());
        }
    }

    class ConnectionWizardDialog : WizardDialogViewModelBase
    {
        public ConnectionWizardDialog(StartupWizard wizard) : base(wizard)
        {
            Wizard = wizard;
        }

        public StartupWizard Wizard { get; }
    }

    sealed class ConsensusServerRpcConnectionDialog : ConnectionWizardDialog
    {
        public ConsensusServerRpcConnectionDialog(StartupWizard wizard, ConsensusServerRpcOptions csro = null) : base(wizard)
        {
            ConnectCommand = new DelegateCommand(Connect);

            // Apply any discovered RPC defaults.
            if (csro != null)
            {
                ConsensusServerNetworkAddress = csro.NetworkAddress;
                ConsensusServerRpcUsername = csro.RpcUser;
                ConsensusServerRpcPassword = csro.RpcPassword;
                ConsensusServerCertificateFile = csro.CertificatePath;
            }
        }

        public string ConsensusServerApplicationName => ConsensusServerRpcOptions.ApplicationName;
        public string CurrencyName => BlockChain.CurrencyName;
        public string ConsensusServerNetworkAddress { get; set; } = "";
        public string ConsensusServerRpcUsername { get; set; } = "";
        public string ConsensusServerRpcPassword { internal get; set; } = "";
        public string ConsensusServerCertificateFile { get; set; } = "";

        public DelegateCommand ConnectCommand { get; }
        private async void Connect()
        {
            try
            {
                ConnectCommand.Executable = false;

                if (string.IsNullOrWhiteSpace(ConsensusServerNetworkAddress))
                {
                    MessageBox.Show("Network address is required");
                    return;
                }
                if (string.IsNullOrWhiteSpace(ConsensusServerRpcUsername))
                {
                    MessageBox.Show("RPC username is required");
                    return;
                }
                if (ConsensusServerRpcPassword.Length == 0)
                {
                    MessageBox.Show("RPC password may not be empty");
                    return;
                }
                if (!File.Exists(ConsensusServerCertificateFile))
                {
                    MessageBox.Show("Certificate file not found");
                    return;
                }

                var walletClient = App.Current.Synchronizer.WalletRpcClient;

                var rpcOptions = new ConsensusServerRpcOptions(ConsensusServerNetworkAddress,
                    ConsensusServerRpcUsername, ConsensusServerRpcPassword, ConsensusServerCertificateFile);
                try
                {
                    await walletClient.StartConsensusRpc(rpcOptions);
                }
                catch (RpcException ex) when (ex.Status.StatusCode == StatusCode.NotFound)
                {
                    var msg = string.Format("Unable to connect to {0}.\n\nConnection could not be established with `{1}`.",
                        ConsensusServerRpcOptions.ApplicationName, ConsensusServerNetworkAddress);
                    MessageBox.Show(msg, "Error");
                    return;
                }
                catch (Exception ex) when (ErrorHandling.IsTransient(ex) || ErrorHandling.IsClientError(ex))
                {
                    MessageBox.Show($"Unable to connect to {ConsensusServerRpcOptions.ApplicationName}.\n\nCheck authentication settings and try again.", "Error");
                    return;
                }

                await Task.Run(() =>
                {
                    // save defaults to a file so that the user doesn't have to type this information again
                    var ini = new IniData();
                    ini.Sections.AddSection("Application Options");
                    ini["Application Options"]["rpcuser"] = ConsensusServerRpcUsername;
                    ini["Application Options"]["rpcpass"] = ConsensusServerRpcPassword;
                    ini["Application Options"]["rpclisten"] = ConsensusServerNetworkAddress;
                    ini["Application Options"]["rpccert"] = ConsensusServerCertificateFile;
                    var appDataDir = Portability.LocalAppData(Environment.OSVersion.Platform,
                                        AssemblyResources.Organization, AssemblyResources.ProductName);
                    var parser = new FileIniDataParser();
                    parser.WriteFile(Path.Combine(appDataDir, "defaults.ini"), ini);
                });

                var walletExists = await walletClient.WalletExistsAsync();
                if (!walletExists)
                {
                    _wizard.CurrentDialog = new PickCreateOrImportSeedDialog(Wizard);
                }
                else
                {
                    // Attempt to open the wallet without any public passphrase.  If this succeeds,
                    // continue by syncing the wallet.  Otherwise, prompt for the public passphrase.
                    try
                    {
                        await walletClient.OpenWallet("");
                        _wizard.CurrentDialog = new OpenExistingWalletActivityProgress(Wizard);
                    }
                    catch (RpcException ex) when (ex.Status.StatusCode == StatusCode.InvalidArgument)
                    {
                        _wizard.CurrentDialog = new PromptPublicPassphraseDialog(Wizard);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error");
            }
            finally
            {
                ConnectCommand.Executable = true;
            }
        }
    }

    sealed class PickCreateOrImportSeedDialog : ConnectionWizardDialog
    {
        public PickCreateOrImportSeedDialog(StartupWizard wizard) : base(wizard)
        {
            CreateWalletCommand = new DelegateCommand(CreateWallet);
            RestoreWalletCommand = new DelegateCommand(RestoreWallet);
        }

        PgpWordList _pgpWordList = new PgpWordList();

        public DelegateCommand CreateWalletCommand { get; }
        private void CreateWallet()
        {
            _wizard.CurrentDialog = new CreateSeedDialog(Wizard, _pgpWordList, this);
        }

        public DelegateCommand RestoreWalletCommand { get; }
        private void RestoreWallet()
        {
            _wizard.CurrentDialog = new ImportSeedDialog(Wizard, _pgpWordList, this);
        }
    }

    sealed class ImportSeedDialog : ConnectionWizardDialog
    {
        public ImportSeedDialog(StartupWizard wizard, PgpWordList pgpWordlist, ConnectionWizardDialog previousDialog) : base(wizard)
        {
            _previousDialog = previousDialog;
            _pgpWordList = pgpWordlist;

            BackCommand = new DelegateCommand(Back);
            ContinueCommand = new DelegateCommand(Continue);
        }

        private readonly ConnectionWizardDialog _previousDialog;
        private readonly PgpWordList _pgpWordList;

        public string ImportedSeed { get; set; } = "";

        public DelegateCommand BackCommand { get; }
        private void Back()
        {
            // TODO: instead of passing around the previous dialog in ctors it would be nice
            // for the wizard to keep track and provide that functionality.
            Wizard.CurrentDialog = _previousDialog;
        }

        public DelegateCommand ContinueCommand { get; }
        private void Continue()
        {
            try
            {
                ContinueCommand.Executable = false;
                var seed = WalletSeed.DecodeAndValidateUserInput(ImportedSeed, _pgpWordList);
                Wizard.CurrentDialog = new PromptPassphrasesDialog(Wizard, seed, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Invalid seed");
            }
            finally
            {
                ContinueCommand.Executable = true;
            }
        }
    }

    sealed class CreateSeedDialog : ConnectionWizardDialog
    {
        public CreateSeedDialog(StartupWizard wizard, PgpWordList pgpWordlist, ConnectionWizardDialog previousDialog) : base(wizard)
        {
            _previousDialog = previousDialog;
            _pgpWordList = pgpWordlist;

            BackCommand = new DelegateCommand(Back);
            ContinueCommand = new DelegateCommand(Continue);

            // false below and remove raise to require a selection.
            ContinueCommand.Executable = true;
        }

        private readonly ConnectionWizardDialog _previousDialog;
        private readonly PgpWordList _pgpWordList;

        private readonly byte[] _randomSeed = WalletSeed.GenerateRandomSeed();

        public string Bip0032SeedHex => Hexadecimal.Encode(_randomSeed);
        public string Bip0032SeedWordList => string.Join(" ", WalletSeed.EncodeWordList(_pgpWordList, _randomSeed));


        public DelegateCommand BackCommand { get; }
        private void Back()
        {
            Wizard.CurrentDialog = _previousDialog;
        }

        public DelegateCommand ContinueCommand { get; }
        private void Continue()
        {
            try
            {
                ContinueCommand.Executable = false;

                Wizard.CurrentDialog = new ConfirmSeedBackupDialog(Wizard, this, _randomSeed, _pgpWordList);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Invalid seed");
            }
            finally
            {
                ContinueCommand.Executable = true;
            }
        }
    }

    sealed class ConfirmSeedBackupDialog : ConnectionWizardDialog
    {
        public ConfirmSeedBackupDialog(StartupWizard wizard, CreateSeedDialog previousDialog,
            byte[] seed, PgpWordList pgpWordlist)
            : base(wizard)
        {
            _previousDialog = previousDialog;
            _seed = seed;
            _pgpWordList = pgpWordlist;

            ConfirmSeedCommand = new DelegateCommand(ConfirmSeed);
            BackCommand = new DelegateCommand(Back);
        }

        private CreateSeedDialog _previousDialog;
        private byte[] _seed;
        private PgpWordList _pgpWordList;

        public string Input { get; set; } = "";

        public DelegateCommand ConfirmSeedCommand { get; }
        private void ConfirmSeed()
        {
            try
            {
                ConfirmSeedCommand.Executable = false;

                // When on testnet, allow clicking through the dialog without any validation.
                if (App.Current.ActiveNetwork == BlockChainIdentity.TestNet)
                {
                    if (Input.Length == 0)
                    {
                        _wizard.CurrentDialog = new PromptPassphrasesDialog(Wizard, _seed, false);
                        return;
                    }
                }

                var decodedSeed = WalletSeed.DecodeAndValidateUserInput(Input, _pgpWordList);
                if (ValueArray.ShallowEquals(_seed, decodedSeed))
                {
                    _wizard.CurrentDialog = new PromptPassphrasesDialog(Wizard, _seed, false);
                }
                else
                {
                    MessageBox.Show("Seed does not match");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Invalid seed");
            }
            finally
            {
                ConfirmSeedCommand.Executable = true;
            }
        }

        public DelegateCommand BackCommand { get; }
        private void Back()
        {
            Wizard.CurrentDialog = _previousDialog;
        }
    }

    sealed class PromptPassphrasesDialog : ConnectionWizardDialog
    {
        public PromptPassphrasesDialog(StartupWizard wizard, byte[] seed, bool restore) : base(wizard)
        {
            _seed = seed;
            _restore = restore;

            CreateWalletCommand = new DelegateCommand(CreateWallet);
        }

        private readonly byte[] _seed;
        private readonly bool _restore;

        public string PrivatePassphrase { private get; set; } = "";
        public string PrivatePassphraseConfirm { private get; set; } = "";

        public DelegateCommand CreateWalletCommand { get; }
        private async void CreateWallet()
        {
            try
            {
                CreateWalletCommand.Executable = false;

                if (string.IsNullOrEmpty(PrivatePassphrase))
                {
                    MessageBox.Show("Private passphrase is required");
                    return;
                }
                if (string.IsNullOrEmpty(PrivatePassphraseConfirm))
                {
                    MessageBox.Show("Confirm private passphrase");
                    return;
                }
                if (!string.Equals(PrivatePassphrase, PrivatePassphraseConfirm))
                {
                    MessageBox.Show("Private passphrases do not match");
                    return;
                }

                var rpcClient = App.Current.Synchronizer.WalletRpcClient;
                await rpcClient.CreateWallet("", PrivatePassphrase, _seed);
                ValueArray.Zero(_seed);

                if (_restore)
                {
                    Wizard.CurrentDialog = new RestoreActivityProgress(Wizard, PrivatePassphrase);
                }
                else
                {
                    Wizard.CurrentDialog = new CreateNewWalletActivityProgress(Wizard);
                }
            }
            finally
            {
                CreateWalletCommand.Executable = true;
            }
        }
    }

    sealed class PromptPublicPassphraseDialog : ConnectionWizardDialog
    {
        public PromptPublicPassphraseDialog(StartupWizard wizard) : base(wizard)
        {
            OpenWalletCommand = new DelegateCommand(OpenWallet);
        }

        public string PublicPassphrase { get; set; } = "";

        public DelegateCommand OpenWalletCommand { get; }
        private async void OpenWallet()
        {
            try
            {
                OpenWalletCommand.Executable = false;
                await App.Current.Synchronizer.WalletRpcClient.OpenWallet(PublicPassphrase);
                Wizard.CurrentDialog = new OpenExistingWalletActivityProgress(Wizard);
            }
            catch (RpcException ex) when (ex.Status.StatusCode == StatusCode.InvalidArgument)
            {
                MessageBox.Show("Incorrect passphrase");
                OpenWalletCommand.Executable = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
                OpenWalletCommand.Executable = true;
            }
        }
    }

    // Action string constants to display while waiting for the wallet to open.
    internal static class ActionStrings
    {
        internal const string DiscoveringAddresses = "discovering addresses";
        internal const string FetchingHeaders = "fetching headers";
        internal const string LoadingTxFilter = "loading transaction filter";
        internal const string Rescanning = "rescanning";
    }

    sealed class RestoreActivityProgress : ConnectionWizardDialog, IActivity
    {
        public RestoreActivityProgress(StartupWizard wizard, string privatePassphrase) : base(wizard)
        {
            _privatePassphrase = privatePassphrase;
        }

        private readonly string _privatePassphrase;

        public string ActionName => "Restoring wallet";

        private string _actionDetail = "";
        public string ActionDetail
        {
            get { return _actionDetail; }
            set { _actionDetail = value; RaisePropertyChanged(); }
        }

        private bool _rescanning = false;
        public bool Rescanning
        {
            get { return _rescanning; }
            set { _rescanning = value; RaisePropertyChanged(); }
        }

        private int _rescannedBlocks = 0;
        public int RescannedBlocks
        {
            get { return _rescannedBlocks; }
            set { _rescannedBlocks = value; RaisePropertyChanged(); }
        }

        private double _rescanPercentCompletion = 0;
        public double RescanPercentCompletion
        {
            get { return _rescanPercentCompletion; }
            set { _rescanPercentCompletion = value; RaisePropertyChanged(); }
        }

        public async Task RunActivityAsync()
        {
            var rpcClient = App.Current.Synchronizer.WalletRpcClient;

            // Subscribing to block notifications is insignificant so don't bother reporting it.  Perform
            // it concurrently with the accounts sync instead.
            ActionDetail = ActionStrings.DiscoveringAddresses;
            await Task.WhenAll(rpcClient.DiscoverAccountsAsync(_privatePassphrase),
                rpcClient.SubscribeToBlockNotificationsAsync());

            ActionDetail = ActionStrings.FetchingHeaders;
            var headersTask = rpcClient.FetchHeadersAsync();
            var txFilterTask = rpcClient.LoadActiveDataFiltersAsync();
            var completedTask = await Task.WhenAny(headersTask, txFilterTask);
            if (completedTask == txFilterTask)
            {
                // Loading the tx filter is likely to finish first unless very many addresses were
                // discovered.  Keep the same action text and wait to finish fetching all headers.
                await headersTask;
            }
            else
            {
                // Headers finished downloading but tx filter hasn't finished being loaded, probably due
                // to very many discovered addresses.  Change the action text to represent this, and then
                // wait for the filter to be loaded before starting the rescan.
                ActionDetail = ActionStrings.LoadingTxFilter;
                await txFilterTask;
            }

            var fetchedHeaders = headersTask.Result.Item1;

            ActionDetail = ActionStrings.Rescanning;
            Rescanning = true;
            // TODO: allow user to provide a hint for where to begin rescan at.
            await rpcClient.RescanFromBlockHeightAsync(0, rescannedThrough =>
            {
                var rescannedBlocks = rescannedThrough + 1;
                RescannedBlocks = rescannedBlocks;
                RescanPercentCompletion = (double)(rescannedBlocks * 100) / fetchedHeaders;
            });

            Wizard.OnFinished();
        }
    }

    sealed class CreateNewWalletActivityProgress : ConnectionWizardDialog, IActivity
    {
        public CreateNewWalletActivityProgress(StartupWizard wizard) : base(wizard)
        {
        }

        public string ActionName => "Creating wallet";

        private string _actionDetail = "";
        public string ActionDetail
        {
            get { return _actionDetail; }
            set { _actionDetail = value; RaisePropertyChanged(); }
        }

        public bool Rescanning => false;
        public int RescannedBlocks => 0;
        public double RescanPercentCompletion => 0F;

        public async Task RunActivityAsync()
        {
            // For newly created wallets, the only startup actions necessary are subscribing to
            // block notifications from the consensus RPC server and fetching headers.  No
            // address/account discovery and no rescanning is necessary because nothing is expected
            // to be discovered for a wallet with a completely new random seed.
            //
            // Despite this, doing address discovery and loading the pregenerated addresses into the
            // transaction filter is still needed, because this is the only way, at time of writing,
            // to initialize the wallet's address pools and receive notifications for addresses the
            // address pool will hand out.

            var rpcClient = App.Current.Synchronizer.WalletRpcClient;

            // Subscribing to block notifications is insignificant so don't bother reporting it.  Perform
            // it concurrently with the address sync instead.
            ActionDetail = ActionStrings.DiscoveringAddresses;
            await Task.WhenAll(rpcClient.DiscoverAddressesAsync(), rpcClient.SubscribeToBlockNotificationsAsync());

            // Loading the tx filter is insignificant compared to fetching headers on a new wallet,
            // so don't bother reporting it.
            ActionDetail = ActionStrings.FetchingHeaders;
            await Task.WhenAll(rpcClient.FetchHeadersAsync(), rpcClient.LoadActiveDataFiltersAsync());

            Wizard.OnFinished();
        }
    }

    sealed class OpenExistingWalletActivityProgress : ConnectionWizardDialog, IActivity
    {
        public OpenExistingWalletActivityProgress(StartupWizard wizard) : base(wizard)
        {
        }

        public string ActionName => "Opening wallet";

        private string _actionDetail = "";
        public string ActionDetail
        {
            get { return _actionDetail; }
            set { _actionDetail = value; RaisePropertyChanged(); }
        }

        private bool _rescanning = false;
        public bool Rescanning
        {
            get { return _rescanning; }
            set { _rescanning = value; RaisePropertyChanged(); }
        }

        private int _rescannedBlocks = 0;
        public int RescannedBlocks
        {
            get { return _rescannedBlocks; }
            set { _rescannedBlocks = value; RaisePropertyChanged(); }
        }

        private double _rescanPercentCompletion = 0;
        public double RescanPercentCompletion
        {
            get { return _rescanPercentCompletion; }
            set { _rescanPercentCompletion = value; RaisePropertyChanged(); }
        }

        public async Task RunActivityAsync()
        {
            var rpcClient = App.Current.Synchronizer.WalletRpcClient;

            // Subscribing to block notifications is insignificant so don't bother reporting it.  Perform
            // it concurrently with the address sync instead.
            ActionDetail = ActionStrings.DiscoveringAddresses;
            await Task.WhenAll(rpcClient.DiscoverAddressesAsync(), rpcClient.SubscribeToBlockNotificationsAsync());

            ActionDetail = ActionStrings.FetchingHeaders;
            var headersTask = rpcClient.FetchHeadersAsync();
            var txFilterTask = rpcClient.LoadActiveDataFiltersAsync();
            var completedTask = await Task.WhenAny(headersTask, txFilterTask);
            if (completedTask == txFilterTask)
            {
                // Loading the tx filter is likely to finish first unless the wallet contains very
                // many addresses and outpoints to watch.  Keep the same action text and wait to
                // finish fetching all headers.
                await headersTask;
            }
            else
            {
                // Headers finished downloading but tx filter hasn't finished being loaded, probably due
                // to very many addresses and unspent outputs being managed by the wallet.  Change the
                // action text to represent this, and then wait for the filter to be loaded before starting
                // the rescan.
                ActionDetail = ActionStrings.LoadingTxFilter;
                await txFilterTask;
            }

            // Only rescan when there were newly fetched headers.
            if (headersTask.Result.Item2 != null)
            {
                var firstNewBlock = headersTask.Result.Item2.Value;
                var previouslySyncedThrough = firstNewBlock.Height - 1;
                var totalBlockCount = previouslySyncedThrough + headersTask.Result.Item1;

                ActionDetail = ActionStrings.Rescanning;
                RescanPercentCompletion = (double)(previouslySyncedThrough * 100) / totalBlockCount;
                Rescanning = true;

                await rpcClient.RescanFromBlockHeightAsync(firstNewBlock.Height, rescannedThrough =>
                {
                    RescannedBlocks = rescannedThrough - previouslySyncedThrough;
                    RescanPercentCompletion = (double)(rescannedThrough * 100) / totalBlockCount;
                });
            }

            Wizard.OnFinished();
        }
    }
}
