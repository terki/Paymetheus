// Copyright (c) 2017 The Decred developers
// Licensed under the ISC license.  See LICENSE file in the project root for full license information.

using System;

namespace Paymetheus.Decred
{
    public sealed class Agenda
    {
        public sealed class Choice
        {
            public string ID { get; set; }
            public string Description { get; set; }
            public ushort Bits { get; set; }
            public bool IsAbstain { get; set; }
            public bool IsNo { get; set; }
        }

        public string ID { get; set; }
        public string Description { get; set; }
        public ushort Mask { get; set; }
        public Choice[] Choices { get; set; }
        public DateTimeOffset StartTime { get; set; }
        public DateTimeOffset ExpireTime { get; set; }
    }
}
