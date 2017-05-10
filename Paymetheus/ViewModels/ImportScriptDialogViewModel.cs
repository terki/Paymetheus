// Copyright (c) 2016 The btcsuite developers
// Copyright (c) 2016 The Decred developers
// Licensed under the ISC license.  See LICENSE file in the project root for full license information.

using Grpc.Core;
using Paymetheus.Decred.Util;
using Paymetheus.Framework;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Paymetheus.ViewModels
{
    public sealed class ImportScriptDialogViewModel : DialogViewModelBase
    {
        private DelegateCommandAsync _importScript;
        public ICommand Execute => _importScript;

        public ImportScriptDialogViewModel(ShellViewModel shell) : base(shell)
        {
            _importScript = new DelegateCommandAsync(ImportScriptAsync);
            _importScript.Executable = false;
        }

        private byte[] _scriptBytes;
        public string ScriptHexString {
            get { return Hexadecimal.Encode(_scriptBytes); }
            set {
                var scriptValid = false;
                try
                {
                    _scriptBytes = Hexadecimal.Decode(value);
                    scriptValid = true;
                }
                finally
                {
                    _importScript.Executable = scriptValid;
                }
            }        
        }

        public bool Rescan { get; set; } = false;

        public string Passphrase { private get; set; } = "";

        private async Task ImportScriptAsync()
        {
            try
            {
                var rpcClient = App.Current.Synchronizer.WalletRpcClient;
                await rpcClient.ImportScriptAsync(_scriptBytes, false, 0, Passphrase);
                if (Rescan)
                {
                    // TODO: hook the rescan progress somewhere so the shell viewmodel can show when the rescan is over.
                    rpcClient.RescanFromBlockHeightAsync(0).ContinueWith(App.Current.WarnIfFailed("Script import rescan failed"));
                }
                HideDialog();
            }
            catch (RpcException ex) when (ex.Status.StatusCode == StatusCode.AlreadyExists)
            {
                MessageBox.Show("Script already exists");
            }
            catch (RpcException ex) when (ex.Status.StatusCode == StatusCode.InvalidArgument)
            {
                MessageBox.Show(ex.Status.Detail);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error");
            }
        }
    }
}
