﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using IEC61850Packet.Utils;
using IEC61850Packet.Goose;
using IEC61850Packet.Mms;


namespace IEC61850Packet.Device
{
    public class ResolveDevice : CaptureFileReaderDevice
    {
        #region Propoerties
        public int PacketCount { get; private set; }
        public bool HasNextPacket
        {
            get
            {
                bool result = false;
                if (currentPacketIndex < PacketCount)
                {
                    result = true;
                }
                return result;
            }
        }
        public event DeviceOpenedEventHandler OnOpened;
        public event DeviceOpeningEventHandler OnOpening;
        #endregion

        #region Private members
        List<Packet> packets = new List<Packet>();
        //List<Type> packetTypes = new List<Type>();
        TpktPacketBuffer tpktBuff;
        CotpPacketBuffer cotpBuff;
        int currentPacketIndex = 1;
        long filePosition = 0;
        private delegate void RaiseEventHandler(object sender,int length);
        private event RaiseEventHandler OnRaising;
        #endregion

        public ResolveDevice(string captureFilename)
            : base(captureFilename)
        {
        }

        public override void Open()
        {
            if (OnOpening == null)
            {
                OnRaising += Refuse_OnRaising;
            }
            else
            {
                OnRaising += Raise_OnRaising;
            }

            base.Open();
            // var dev = new CaptureFileReaderDevice(@"..\..\CapturedFiles\20140813-150920_0005ED9B-50+60_MMS.pcap");
            // dev.Filter = "ip src 198.121.0.92 and tcp"; // 92 or 115

            RawCapture rawCapture;
            rawCapture = base.GetNextPacket();
            while (rawCapture != null)
            {
                Packet p = Packet.ParsePacket(rawCapture.LinkLayerType, rawCapture.Data);
                try
                {
                    TcpPacket tcp = p.Extract<TcpPacket>();
                    if (tcp != null && tcp.PayloadData.Length > 0)
                    {
                        ExtractUpperPacket(tcp);
                    }
                    else
                    {
                        // UNDONE: For GOOSE and SV or null TCP
                        EthernetPacket ether = p.Extract<EthernetPacket>();
                        ExtractEthernetPacket(ether);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("No. {0}: {1}\nTPKT buffer count: {2}.", currentPacketIndex, ex.Message, tpktBuff.Reassembled.Count);
#if DEBUG
                    Console.WriteLine(ex.StackTrace);
#endif
                }
                finally
                {
                    rawCapture = base.GetNextPacket();
                    currentPacketIndex++;
                    OnRaising(this,p.Bytes.Length);
                    //OnOpening(this, new DeviceOpeningEventArgs((double)p.Bytes.Length / base.FileSize));
                }            
            }

            // TODO: Reset the pos, raise the Opened Event
            currentPacketIndex = 0;
            PacketCount = packets.Count;
            if (OnOpened != null)
            {
                OnOpened(this, new DeviceOpenedEventArgs());
            }
        }
               
        public new Packet GetNextPacket()
        {
            if (currentPacketIndex <= PacketCount)
            {
                return packets[currentPacketIndex++];
            }
            else
            {
                throw new InvalidOperationException("No more packets.");
            }
        }

        //public Type GetPacketType()
        //{
        //    return packetTypes[currentPacketIndex];
        //}

        public override void Close()
        {
            OnOpened = null;
            OnOpening = null;
            OnRaising = null;
            tpktBuff.Reset();
            tpktBuff = null;
            cotpBuff.Reset();
            cotpBuff = null;
            //packets.Clear();
            //packets = null;
            //PacketCount = 0;
            //filePosition = 0;
            currentPacketIndex = 0;
            base.Close();
        }

        private void ExtractUpperPacket(TcpPacket tcp)
        {

            TpktFileds tf = new TpktFileds();
            string srcIp = tcp.ParentPacket<IPv4Packet>().SourceAddress.ToString();
            tpktBuff = TpktPacketBufferFactory.GetBuffer(srcIp);
            if (tpktBuff.Count > 0 && !tpktBuff.IsReassembled)
            {
                tf.LeadWithSegment = true;
                tf.LeadingSegmentLength = tpktBuff.Last.NextFrameSegmentLength;
            }

            byte[] header = tcp.PayloadData.Take(TpktFileds.HeaderLength).ToArray();
            if (tf.LeadWithSegment || TpktPacket.IsTpkt(header))
            {
                TpktPacket tpkt = new TpktPacket(tcp.PayloadData, tcp, tf);
                if (tpkt.PayloadPacket != null || tpkt.PayloadData != null)
                {
                    tpktBuff.Add(tpkt);
                    if (tpktBuff.IsReassembled)
                    {
                        foreach (TpktPacket reassTpkt in tpktBuff.Reassembled)
                        {
                            CotpPacket cotp = reassTpkt.Extract<CotpPacket>();
                            if (cotp.Type == CotpPacket.TpduType.DataTransfer)
                            {
                                cotpBuff = CotpPacketBufferFactory.GetBuffer(srcIp);
                                cotpBuff.Add(cotp);
                                if (cotpBuff.IsReassembled)
                                {
                                    CotpPacket reassCotp = cotpBuff.Reassembled;
                                    var session = new OsiSessionPacket(reassCotp.PayloadData, reassCotp);
                                    //packets.Add();
                                    var mms = session.Extract<MmsPacket>();
                                    if (mms != null)
                                    {
                                        packets.Add(mms);
                                      //  packetTypes.Add(typeof(MmsPacket));
                                    }
                                    else
                                    {
                                        packets.Add(session);
                                       // packetTypes.Add(typeof(OsiSessionPacket));
                                    }

                                    cotpBuff.Reset();

                                    #region For debug
#if DEBUG &&  SHOW_DETAILS
                                    MmsPacket mms = (MmsPacket)packets.Last().Extract(typeof(MmsPacket));
                                    if (mms.Pdu is UnconfirmedPdu)
                                    {
                                        var pdu = mms.Pdu as UnconfirmedPdu;
                                        string dsRef = pdu.Service.InformationReport.ListOfAccessResult[3].Success.GetValue<IEC61850Packet.Asn1.Types.VisibleString>().Value;
                                        Console.WriteLine("No. {0}: {1}", currentPacketIndex, dsRef);
                                    }
#endif
                                    #endregion
                                }

                            }
                        }
                        tpktBuff.Reset();
                    }
                }
            }

        }

        private void ExtractEthernetPacket(EthernetPacket ether)
        {
            switch (ether.Type)
            {
                case EthernetPacketType.Goose:
                    ether.PayloadPacket = new GoosePacket(ether.PayloadData, ether);
                    int len = ether.PayloadPacket.Extract<GoosePacket>().APDU.Bytes.Length;
                    packets.Add(ether.PayloadPacket);
                  //  packetTypes.Add(typeof(GoosePacket));
                    break;
                case EthernetPacketType.Sv:
                    // UNDONE: SV construct
                    // packets.Add(ether);
                    break;
                case EthernetPacketType.Gse:
                    break;
                default:
                    // Unknown packet
                    break;
            }

        }

        private void Refuse_OnRaising(object sender, int length)
        {

        }

        private void Raise_OnRaising(object sender, int length)
        {
            filePosition += length;
            OnOpening(this, new DeviceOpeningEventArgs((double)filePosition / base.FileSize));
        }
   
    }
}
