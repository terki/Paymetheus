// Copyright (c) 2016 The Decred developers
// Licensed under the ISC license.  See LICENSE file in the project root for full license information.

using CsvHelper;
using Paymetheus.Decred;
using Paymetheus.Decred.Wallet;
using Paymetheus.Framework;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Paymetheus.ViewModels
{
    public sealed class TransactionHistoryViewModel : ViewModelBase
    {
        public TransactionHistoryViewModel()
        {
            var synchronizer = ViewModelLocator.SynchronizerViewModel as SynchronizerViewModel;
            if (synchronizer == null)
                return;

            _selectedAccount = synchronizer.Accounts[0];
            Task.Run(() => PopulateHistoryAsync(_selectedAccount.Account));
            ExportTransactionHistoryCommand = new DelegateCommand(ExportTransactionHistory);
        }

        private AccountViewModel _selectedAccount;
        public AccountViewModel SelectedAccount
        {
            get { return _selectedAccount; }
            set
            {
                _selectedAccount = value;

                Transactions.Clear();
                Task.Run(() => PopulateHistoryAsync(value.Account));

                RaisePropertyChanged();
            }
        }

        private Amount _debitSum;
        public Amount DebitSum
        {
            get { return _debitSum; }
            private set { _debitSum = value; RaisePropertyChanged(); }
        }

        private Amount _creditSum;
        public Amount CreditSum
        {
            get { return _creditSum; }
            private set { _creditSum = value; RaisePropertyChanged(); }
        }

        public sealed class HistoryItem
        {
            public HistoryItem(TransactionViewModel txvm, Amount accountDebit, Amount accountCredit, Amount runningBalance)
            {
                Transaction = txvm;
                AccountDebit = accountDebit;
                AccountCredit = accountCredit;
                RunningBalance = runningBalance;
            }

            public TransactionViewModel Transaction { get; }
            public Amount AccountDebit { get; }
            public Amount AccountCredit { get; }
            public Amount AccountDebitCredit => AccountDebit + AccountCredit;
            public Amount RunningBalance { get; }
        }

        public ObservableCollection<HistoryItem> Transactions { get; } = new ObservableCollection<HistoryItem>();

        // TODO: Figure out what to do with exceptions.  another message box?
        private async Task PopulateHistoryAsync(Account account)
        {
            var synchronizer = ViewModelLocator.SynchronizerViewModel as SynchronizerViewModel;
            var walletMutex = synchronizer?.WalletMutex;
            if (walletMutex == null)
                return;

            using (var walletGuard = await walletMutex.LockAsync())
            {
                Amount totalDebits = 0;
                Amount totalCredits = 0;
                foreach (var histItem in EnumerateAccountTransactions(walletGuard.Instance, account))
                {
                    totalDebits += histItem.AccountDebit;
                    totalCredits += histItem.AccountCredit;
                    Application.Current.Dispatcher.Invoke(() => Transactions.Add(histItem));
                }

                DebitSum = totalDebits;
                CreditSum = totalCredits;
            }
        }

        private static IEnumerable<HistoryItem> EnumerateAccountTransactions(Wallet wallet, Account account)
        {
            Amount runningBalance = 0;

            // RecentTransactions currently includes every transaction.
            // This will change in a future release, but for now don't bother using RPC to fetch old transactions.
            // Iterate through them, oldest first.
            foreach (var block in wallet.RecentTransactions.MinedTransactions)
            {
                var minedAccountTxs = block.Transactions.
                    Select(tx => AccountTransaction.Create(account, tx)).
                    Where(atx => atx.HasValue).
                    Select(atx => atx.Value);
                foreach (var accountTx in minedAccountTxs)
                {
                    var txvm = new TransactionViewModel(wallet, accountTx.Transaction, block.Identity);
                    runningBalance += accountTx.DebitCredit;
                    yield return new HistoryItem(txvm, accountTx.Debit, accountTx.Credit, runningBalance);
                }
            }

            var unminedAccountTxs = wallet.RecentTransactions.UnminedTransactions.
                Select(tx => AccountTransaction.Create(account, tx.Value)).
                Where(atx => atx.HasValue).
                Select(atx => atx.Value).
                OrderBy(atx => atx.Transaction.SeenTime);
            foreach (var accountTx in unminedAccountTxs)
            {
                var txvm = new TransactionViewModel(wallet, accountTx.Transaction, BlockIdentity.Unmined);
                runningBalance += accountTx.DebitCredit;
                yield return new HistoryItem(txvm, accountTx.Debit, accountTx.Credit, runningBalance);
            }
        }

        public void AppendNewTransactions(Wallet wallet, List<Tuple<WalletTransaction, BlockIdentity>> txs)
        {
            var account = SelectedAccount.Account;
            var totalDebits = DebitSum;
            var totalCredits = CreditSum;
            var runningBalance = totalDebits + totalCredits;
            foreach (var tx in txs)
            {
                var accountTxOption = AccountTransaction.Create(account, tx.Item1);
                if (accountTxOption == null)
                    continue;
                var accountTx = accountTxOption.Value;
                var txvm = new TransactionViewModel(wallet, accountTx.Transaction, tx.Item2);
                totalDebits += accountTx.Debit;
                totalCredits += accountTx.Credit;
                runningBalance += accountTx.DebitCredit;
                var histItem = new HistoryItem(txvm, accountTx.Debit, accountTx.Credit, runningBalance);
                App.Current.Dispatcher.Invoke(() => Transactions.Add(histItem));
            }

            DebitSum = totalDebits;
            CreditSum = totalCredits;
        }

        public ICommand ExportTransactionHistoryCommand { get; }

        private void ExportTransactionHistory()
        {
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.FileName = "";
            dlg.DefaultExt = ".csv";
            dlg.Filter = "Comma separated file|*.csv";
            dlg.Title = "Export Transaction History";

            Nullable<bool> result = dlg.ShowDialog();
            if (result == true)
            {
                // Save document
                string filename = dlg.FileName;

                TextWriter textWriter = new StreamWriter(filename);

                var csv = new CsvWriter(textWriter);
                csv.Configuration.QuoteAllFields = true;
                csv.WriteField("Confirmed");
                csv.WriteField("Date");
                csv.WriteField("Type");
                csv.WriteField("Label");
                csv.WriteField("Address");
                csv.WriteField("Amount (DCR)");
                csv.WriteField("ID");
                csv.NextRecord();

                bool multiOutputWarningShown = false;

                foreach (var item in Transactions.Reverse())
                {
                    var outputs = item.Transaction.Outputs.Where(x => x.Destination != "Change" && x.Destination != "Non-address output");
                    csv.WriteField((item.Transaction.ConfirmationStatus == ConfirmationStatus.Confirmed).ToString().ToLower());
                    csv.WriteField(item.Transaction.LocalSeenTime.ToString("s"));
                    string txType = "Unknown";
                    switch (item.Transaction.Category)
                    {
                        case TransactionCategory.Receive: txType = "Received with"; break;
                        case TransactionCategory.Send: txType = "Sent to"; break;
                        // TODO: are these compatible with e g bitcoin.tax import?
                        case TransactionCategory.TicketPurchase: txType = "Ticket purchase"; break;
                        case TransactionCategory.TicketRevocation: txType = "Ticket revocation"; break;
                        case TransactionCategory.Vote: txType = "Vote"; break;
                    }
                    // if (item.Transaction.GroupedOutputs.Count > 0 && !item.Transaction.GroupedOutputs.Any(x => x.Destination != "Change"))
                    if (outputs.Count() == 0)
                    {
                        // txType = "Payment to yourself";
                        txType = item.AccountDebitCredit>=0?"Received with":"Sent to";
                        csv.WriteField(txType);
                        csv.WriteField(""); // Label
                        csv.WriteField(SelectedAccount.AccountProperties.AccountName); // Address
                    }
                    else
                    {
                        csv.WriteField(txType);
                        csv.WriteField(""); // Label
                        if (outputs.Count() > 1)
                        {
                            csv.WriteField("Multiple addresses"); // Address
                            if (!multiOutputWarningShown)
                            {
                                MessageBox.Show("Multiple non-change outputs transactions not supported yet");
                                multiOutputWarningShown = true;
                            }
                        }
                        else
                        {
                            csv.WriteField(outputs.First().Address); // Address
                        }
                    }
                    csv.WriteField(Denomination.Decred.DoubleFromAmount(item.AccountDebitCredit).ToString("0.00000000",CultureInfo.InvariantCulture));
                    csv.WriteField(item.Transaction.TxHash);
                    csv.NextRecord();
                }
                textWriter.Close();
            }
        }
    }
}
