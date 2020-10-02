﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using evtx.Tags;
using Force.Crc32;
using NLog;

namespace evtx
{
    public class ChunkInfo
    {

        public ChunkInfo(byte[] chunkBytes, long absoluteOffset, int chunkNumber)
        {
            var l = LogManager.GetLogger("ChunkInfo");

            l.Trace(
                $"\r\n-------------------------------------------- NEW CHUNK at 0x{absoluteOffset:X} ------------------------------------------------\r\n");

            ChunkBytes = chunkBytes;
            AbsoluteOffset = absoluteOffset;
            ChunkNumber = chunkNumber;

            ErrorRecords = new Dictionary<long, string>();

            EventRecords = new List<EventRecord>();

            FirstEventRecordNumber = BitConverter.ToInt64(chunkBytes, 0x8);
            LastEventRecordNumber = BitConverter.ToInt64(chunkBytes, 0x10);
            FirstEventRecordIdentifier = BitConverter.ToInt64(chunkBytes, 0x18);
            LastEventRecordIdentifier = BitConverter.ToInt64(chunkBytes, 0x20);

            if (FirstEventRecordIdentifier == -1)
            {
                return;
            }

            var tableOffset = BitConverter.ToUInt32(chunkBytes, 0x28);

            LastRecordOffset = BitConverter.ToUInt32(chunkBytes, 0x2C);
            FreeSpaceOffset = BitConverter.ToUInt32(chunkBytes, 0x30);

            //TODO how to calculate this? across what data? all event records?
            var crcEventRecordsData = BitConverter.ToUInt32(chunkBytes, 0x34);

            Crc = BitConverter.ToInt32(chunkBytes, 0x7c);

            var inputArray = new byte[120 + 384 + 4];
            Buffer.BlockCopy(chunkBytes, 0, inputArray, 0, 120);
            Buffer.BlockCopy(chunkBytes, 128, inputArray, 120, 384);

            Crc32Algorithm.ComputeAndWriteToEnd(inputArray); // last 4 bytes contains CRC
            CalculatedCrc = BitConverter.ToInt32(inputArray, inputArray.Length - 4);

            var index = 0;
            var tableData = new byte[0x100];
            Buffer.BlockCopy(chunkBytes, (int) tableOffset, tableData, 0, 0x100);

            StringTableEntries = new Dictionary<uint, StringTableEntry>();

            var stringOffsets = new List<uint>();

            var ticksForTimeDelta = 10000000 * EventLog.TimeDiscrepancyThreshold; //10000000 == ticks in a second

            while (index < tableData.Length)
            {
                var stringOffset = BitConverter.ToUInt32(tableData, index);
                index += 4;
                if (stringOffset == 0)
                {
                    continue;
                }

                stringOffsets.Add(stringOffset);
            }

            foreach (var stringOffset in stringOffsets)
            {
                GetStringTableEntry(stringOffset);
            }

            l.Trace("String table entries");
            foreach (var stringTableEntry in StringTableEntries.Keys.OrderBy(t => t))
            {
                l.Trace(StringTableEntries[stringTableEntry]);
            }

            l.Trace("");

            var templateTableData = new byte[0x80];
            Buffer.BlockCopy(chunkBytes, 0x180, templateTableData, 0, 0x80);

            var tableTemplateOffsets = new List<uint>();

            index = 0;
            while (index < templateTableData.Length)
            {
                var templateOffset = BitConverter.ToUInt32(templateTableData, index);
                index += 4;
                if (templateOffset == 0)
                {
                    continue;
                }

                //the actual table definitions live at this Offset + 0x1000 for header, - 10 bytes for some reason.
                //This is where the 0xc op code will be
                tableTemplateOffsets.Add(templateOffset);
            }

            Templates = new Dictionary<int, Template>();

            //to get all the templates and cache them
            foreach (var tableTemplateOffset in tableTemplateOffsets.OrderBy(t => t))
            {
                var actualOffset = absoluteOffset + tableTemplateOffset - 10; //yea, -10
                index = (int) tableTemplateOffset - 10;

                l.Trace(
                    $"Chunk absoluteOffset: 0x{AbsoluteOffset:X} tableTemplateOffset: 0x{tableTemplateOffset:X} actualOffset: 0x {actualOffset:X} chunkBytes[index]: 0x{chunkBytes[index]:X} LastRecordOffset 0x{LastRecordOffset:X} FreeSpaceOffset 0x{FreeSpaceOffset:X}");

                var template = GetTemplate(index);

                if (template == null)
                {
                    l.Trace(
                        $"Implausable template at actual offset: 0x{actualOffset} tableTemplateOffset 0x{tableTemplateOffset:X} FreeSpaceOffset: 0x{FreeSpaceOffset} chunk absolute offset: 0x{AbsoluteOffset:X}");
                    continue;
                }

                if (Templates.ContainsKey(template.TemplateOffset) == false)
                {
                    Templates.Add(template.TemplateOffset - 0x18, template);
                }

                if (template.NextTemplateOffset <= 0)
                {
                    continue;
                }

                var nextTemplateId = template.NextTemplateOffset;

                while (nextTemplateId > 0)
                {
                    var bbb = GetTemplate(nextTemplateId - 10);

                    nextTemplateId = bbb.NextTemplateOffset;

                    if (Templates.ContainsKey(bbb.TemplateOffset) == false)
                    {
                        Templates.Add(bbb.TemplateOffset - 0x18, bbb);
                    }
                }
            }

            l.Trace("Template definitions");
            foreach (var esTemplate in Templates.OrderBy(t => t.Key))
            {
                l.Trace($"key: 0x{esTemplate.Key:X4} {esTemplate.Value}");
            }

            l.Trace("");

            index = (int) tableOffset + 0x100 + 0x80; //get to start of event Records

            l.Trace($"\r\nChunk data before processing records: {this}");

            const int recordSig = 0x2a2a;

            long lastRecordNumber = 0;

            while (index < chunkBytes.Length)
            {
                var sig = BitConverter.ToInt32(chunkBytes, index);

                if (sig != recordSig)
                {
                    l.Trace(
                        $"Found an invalid signature at 0x{absoluteOffset + index:X}");
                    break;
                }

                var recordOffset = index;

                //do not read past the last known defined record
                if (recordOffset - absoluteOffset > LastRecordOffset)
                {
                    l.Trace(
                        "Reached last record offset. Stopping");
                    break;
                }

                var recordSize = BitConverter.ToUInt32(chunkBytes, index + 4);

                var recordNumber = BitConverter.ToInt64(chunkBytes, index + 8);

                try
                {
                    if (recordNumber < FirstEventRecordIdentifier || recordNumber > LastEventRecordIdentifier)
                    {
                        //outside known good range, so ignore
                        l.Trace(
                            $"Record at offset 0x{AbsoluteOffset + recordOffset:X} falls outside valid record identifier range. Skipping");
                        break;
                    }

                    var ms = new MemoryStream(chunkBytes, index, (int) recordSize);
                    var br = new BinaryReader(ms, Encoding.UTF8);

                    index += (int) recordSize;

                    var er = new EventRecord(br, recordOffset, this);

                    EventRecords.Add(er);

                     lastRecordNumber = er.RecordNumber;

                    if (er.ExtraDataOffset > 0)
                    {
                        try
                        {
                            //hidden data!
                            recordSize = BitConverter.ToUInt32(ms.ToArray(), (int)er.ExtraDataOffset + 4);

                            recordNumber = BitConverter.ToInt64(ms.ToArray(), (int)er.ExtraDataOffset + 8);

                            if (recordNumber != lastRecordNumber)
                            {

                                ms = new MemoryStream(ms.ToArray(), (int)er.ExtraDataOffset, (int)recordSize);
                                br = new BinaryReader(ms, Encoding.UTF8);

                                er = new EventRecord(br, (int) (recordOffset + er.ExtraDataOffset), this);
                                er.HiddenRecord = true;

                                l.Warn($"Record #: {er.RecordNumber} (timestamp: {er.TimeCreated:yyyy-MM-dd HH:mm:ss.fffffff}): Warning! A hidden record was found! Possible DanderSpritz use detected!");

                                EventRecords.Add(er);
                            }

                        }
                        catch (Exception e)
                        {
                            //oh well, we tried
                            //l.Warn($"Error when attemping to recover possible hidden record: {e.Message}");
                        }
                        
                    }

                   

                    //ticksForTimeDelta == totalticks for discrepancy value
                    if (EventLog.LastSeenTicks > 0 && EventLog.LastSeenTicks - ticksForTimeDelta > er.TimeCreated.Ticks)
                    {
                        l.Warn($"Record #: {er.RecordNumber} (timestamp: {er.TimeCreated:yyyy-MM-dd HH:mm:ss.fffffff}): Warning! Time just went backwards! Last seen time before change: {new DateTimeOffset(EventLog.LastSeenTicks,TimeSpan.Zero).ToUniversalTime():yyyy-MM-dd HH:mm:ss.fffffff}");
                    }

                    EventLog.LastSeenTicks = er.TimeCreated.Ticks;

                }
                catch (Exception e)
                {
                    l.Trace(
                        $"First event record ident-num: {FirstEventRecordIdentifier}-{FirstEventRecordNumber} Last event record ident-num: {LastEventRecordIdentifier}-{LastEventRecordNumber} last record offset 0x{LastRecordOffset:X}");
                    l.Error(
                        $"Record error at offset 0x{AbsoluteOffset + recordOffset:X}, record #: {recordNumber} error: {e.Message}");

                    if (ErrorRecords.ContainsKey(recordNumber) == false)
                    {
                        ErrorRecords.Add(recordNumber, e.Message);
                    }
                }
            }
        }

        public uint LastRecordOffset { get; }
        public uint FreeSpaceOffset { get; }

        public Dictionary<int, Template> Templates { get; }

        public List<EventRecord> EventRecords { get; }
        public Dictionary<long, string> ErrorRecords { get; }

        public Dictionary<uint, StringTableEntry> StringTableEntries { get; }

        public int Crc { get; }
        public int CalculatedCrc { get; }

        public long LastEventRecordIdentifier { get; }

        public long FirstEventRecordIdentifier { get; }

        public long LastEventRecordNumber { get; }

        public long FirstEventRecordNumber { get; }

        public byte[] ChunkBytes { get; set; }
        public long AbsoluteOffset { get; }
        public int ChunkNumber { get; }

        public StringTableEntry GetStringTableEntry(uint offset)
        {
            if (StringTableEntries.ContainsKey(offset))
            {
                return StringTableEntries[offset];
            }

            var index = (int) offset + 4; //unknown bytes, so skip
            var hash = BitConverter.ToUInt16(ChunkBytes, index);
            index += 2;
            var stringLen = BitConverter.ToUInt16(ChunkBytes, index);
            index += 2;
            var stringVal = Encoding.Unicode.GetString(ChunkBytes, index, stringLen * 2);

            var entrySize = 4 + 2 + 2 + stringLen * 2 + 2; //unknown + hash + len + null

            StringTableEntries.Add(offset, new StringTableEntry(offset, hash, stringVal, entrySize));

            return new StringTableEntry(offset, hash, stringVal, entrySize);
        }

        public Template GetTemplate(int startingOffset)
        {
            if (Templates.ContainsKey(startingOffset))
            {
                return Templates[startingOffset];
            }

            var index = startingOffset;

            //verify we actually have a template sitting here
            if (ChunkBytes[index] != 0x0C)
            {
                //if the template list is fubar, it may still be ok!
                if (ChunkBytes[index - 10] != 0x0C)
                {
                    return null;
                }

                //in some cases the $(*&*&$#&*$ template offset list is full of garbage, so this is a fallback
                {
                    index = startingOffset - 10;
                }
            }

            index += 1; //go past op code

            var unusedVersion = ChunkBytes[index];
            index += 1;

            var templateId = BitConverter.ToInt32(ChunkBytes, index);
            index += 4;

            var templateOffset = BitConverter.ToInt32(ChunkBytes, index);
            index += 4;

            if (templateOffset == 0x0)
            {
                return null;
            }

            var nextTemplateOffset = BitConverter.ToInt32(ChunkBytes, index);
            index += 4;

            var gb = new byte[16];
            Buffer.BlockCopy(ChunkBytes, index, gb, 0, 16);
            index += 16;
            var g = new Guid(gb);

            var length = BitConverter.ToInt32(ChunkBytes, index);
            index += 4;

            var templateBytes = new byte[length];
            Buffer.BlockCopy(ChunkBytes, index, templateBytes, 0, length);

            var br = new BinaryReader(new MemoryStream(templateBytes));

            var l = LogManager.GetLogger("T");

            l.Trace(
                $"\r\n-------------- NEW TEMPLATE at 0x{AbsoluteOffset + templateOffset - 10:X} ID: 0x{templateId:X} templateOffset: 0x{templateOffset:X} ---------------------");

            //the offset + 18 gets us to the start of the actual template (0x0f 0x01, etc)
            return new Template(templateId, templateOffset + 0x18, g, br, nextTemplateOffset,
                AbsoluteOffset + templateOffset, this);
        }

        public override string ToString()
        {
            return
                $"Chunk absolute offset 0x{AbsoluteOffset:X8} Chunk #: {ChunkNumber.ToString().PadRight(5)} FirstEventRecordNumber: {FirstEventRecordNumber.ToString().PadRight(8)} LastEventRecordNumber: {LastEventRecordNumber.ToString().PadRight(8)} FirstEventRecordIdentifier: {FirstEventRecordIdentifier.ToString().PadRight(8)} LastEventRecordIdentifier: {LastEventRecordIdentifier.ToString().PadRight(8)}";
        }
    }
}