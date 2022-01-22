﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace evtx.Tags;

public class NormalSubstitution : IBinXml
{
    public NormalSubstitution(long recordPosition, BinaryReader dataStream)
    {
        RecordPosition = recordPosition;
        Size = 4;

        SubstitutionId = dataStream.ReadInt16();
        ValueType = (TagBuilder.ValueType) dataStream.ReadByte();

        Log.Verbose("{This}",this);
    }

    public short SubstitutionId { get; }
    public TagBuilder.ValueType ValueType { get; }

    public long RecordPosition { get; }
    public long Size { get; }

    public string AsXml(List<SubstitutionArrayEntry> substitutionEntries, long parentOffset)
    {
//            var subEntry = substitutionEntries.Single(t => t.Position == SubstitutionId);
//            if (subEntry.ValType == TagBuilder.ValueType.NullType)
//            {
//                return "";
//            }
        var val = substitutionEntries.Single(t => t.Position == SubstitutionId).GetDataAsString();

        return val;
    }

    public TagBuilder.BinaryTag TagType => TagBuilder.BinaryTag.NormalSubstitution;

    public override string ToString()
    {
        return $"Normal substitution. Id: {SubstitutionId} Value type: {ValueType}";
    }
}