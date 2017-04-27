// Copyright (c) 2016 The btcsuite developers
// Copyright (c) 2016 The Decred developers
// Licensed under the ISC license.  See LICENSE file in the project root for full license information.

using System;

namespace Paymetheus.Decred
{
    public sealed class BlockChainIdentity
    {
        public static readonly BlockChainIdentity MainNet = new BlockChainIdentity
        (
            name: "mainnet",
            network: 0xd9b400f9,
            maturity: 256,
            coinType: 20,
            svh: 4096,
            sdiffRetargetInterval: 144
        );

        public static readonly BlockChainIdentity TestNet = new BlockChainIdentity
        (
            name: "testnet",
            network: 0x48e7a065,
            maturity: 16,
            coinType: 11,
            svh: 768,
            sdiffRetargetInterval: 144
        );

        public static readonly BlockChainIdentity SimNet = new BlockChainIdentity
        (
            name: "simnet",
            network: 0x12141c16,
            maturity: 16,
            coinType: 115,
            svh: 16 + (64 * 2),
            sdiffRetargetInterval: 8
        );

        private BlockChainIdentity(string name, uint network, int maturity, uint coinType, int svh, int sdiffRetargetInterval)
        {
            Name = name;
            Network = network;
            Maturity = maturity;
            Bip44CoinType = coinType;
            StakeValidationHeight = svh;
            StakeDifficultyRetargetInterval = sdiffRetargetInterval;
        }

        public string Name { get; }
        public uint Network { get; }
        public int Maturity { get; }
        public uint Bip44CoinType { get; }
        public int StakeValidationHeight { get; }
        public int StakeDifficultyRetargetInterval { get; }

        public static BlockChainIdentity FromNetworkBits(uint network)
        {
            if (network == MainNet.Network)
                return MainNet;
            else if (network == TestNet.Network)
                return TestNet;
            else if (network == SimNet.Network)
                return SimNet;
            else
                throw new UnknownBlockChainException($"Unrecognized or unsupported blockchain described by network ID `{network}`");
        }
    }

    public class UnknownBlockChainException : Exception
    {
        public UnknownBlockChainException(string message) : base(message) { }

        public UnknownBlockChainException(BlockChainIdentity identity)
            : base($"Unknown blockchain `{identity.Name}`")
        { }
    }
}
