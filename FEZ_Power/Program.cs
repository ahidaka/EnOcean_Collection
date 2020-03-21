/*                                                           */
/* ワイヤレスボード(EO00701)用USBizi100プログラム 2012/02/25 */
/*                                                           */
/* GHI NETMF v4.1 SDK Version 1.0.18(USBizi V 4.1.8.0)       */
/*                                                           */

using System;
using System.Collections;
using System.IO.Ports;
using System.Threading;
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using GHIElectronics.NETMF.Hardware;

namespace FEZ_Power
{
    using Main = ThreadManagment;
    using Tx = Transmit;
    using Sx = StringExtensions;
 
    public class Program
    {
        public static void Main()
        {
            ThreadManagment tM = new ThreadManagment();
            tM.Run();
            Thread.Sleep(Timeout.Infinite);
        }
    }

    public class ThreadManagment
    {
        private AutoResetEvent actionEvent = new AutoResetEvent(false);
        private Queue actionQueue = new Queue();
        private struct ActionStruct
        {
            public Action action;
            public byte[] destination;
            public byte[] parameter;
        };
        private Queue responseQueue = new Queue();
        private struct ResponseStruct
        {
            public Transmit.Response r;
            public byte[] destination;
        };

        private Transmit tx;
        static SerialPort debugPort = new SerialPort(Serial.COM1, 9600, Parity.None, 8, StopBits.One);
        static SerialPort powerPort = new SerialPort(Serial.COM2, 9600, Parity.None, 8, StopBits.One);
        static SerialPort wirelessPort = new SerialPort(Serial.COM3, 57600, Parity.None, 8, StopBits.One);
        static OutputPort relayPort = new OutputPort((Cpu.Pin)USBizi.Pin.IO11, true);

        Thread wirelessThread;
        Thread powerThread;
        Thread actionThread;

        const Byte SYNCBYTE  = 0x55;
        //const Byte SPCODE = 0x20;
        const Byte CRCODE = 0x0D;
        const Byte LFCODE = 0x0A;

        //
        //
        //
        private int mSeq = 0;

        private enum Action
        {
            // for Response
            NoAction,
            TransmitPower,
            SetVersion,
            TransmitResponse,
            ReadRepeater,
            // for Receive Telegram 
            WriteRepeater,
            AnswerRMC,
            TransmitError
        };

        public enum PacketType
        {
            Radio = 0x01,
            Response = 0x02,
            RadioSubTel = 0x03,
            Event = 0x04,
            CommonCommand = 0x05,
            SmartAckCommand = 0x06,
            RemoteManCommand = 0x07
        };

        public enum RMC
        {
            Unlock = 0x001,
            Lock = 0x002,
            SetCode = 0x003,
            QueryID = 0x004,
            QueryIDAnswer = 0x604,
            Action = 0x005,
            Ping = 0x006,
            PingAnswer = 0x606,
            QueryFunction = 0x007,
            QueryFunctionAnswer = 0x607,
            QueryStatus = 0x008,
            QueryStatusAnswer = 0x608,
            RPCRepeater = 0x345,
            RPCRepeaterAnswer = 0x745
        }

        public const byte porgRPS = 0xF6;
        public const byte porg1BS = 0xD5;
        public const byte porg4BS = 0xA5;
        public const byte porgSYS_EX = 0xC5;

        public const byte maxTelegram = 48;

        public const byte manufactureID = 0x00b;
        public const byte eep21Func = 0x37;
        public const byte eep21Type = 0x01;
        //
        private byte[] myID = new byte[4];
        private byte[] AppVersion = new byte[4];
        private byte[] APIVersion = new byte[4];
        private byte[] ChipVersion = new byte[4];

        private int lastPower;
        //
        public ThreadManagment()
        {
            debugPort.Open();
            powerPort.Open();
            wirelessPort.Open();

            wirelessThread = new Thread(WirelessReceiveThread);
            powerThread = new Thread(WirelessDataSendThread);
            actionThread = new Thread(ActionThread);
            tx = new Transmit(wirelessPort, responseQueue);
        }

        public void Run()
        {
            DebugPrint("Start...\r\n");

            wirelessThread.Start();
            powerThread.Start();
            actionThread.Start();

            Thread.Sleep(397);
            GetVersion();
        }

        private void GetVersion()
        {
            ResponseStruct res = new ResponseStruct();
            byte[] command = { 3 }; // CO_RD_VERSION
            res.r = (Transmit.Response)command[0];
            res.destination = Tx.broadcast;
            tx.TransmitCommand(res, command);
        }

        public void DebugPrint(String str)
        {
            Byte[] debugBuffer = System.Text.UTF8Encoding.UTF8.GetBytes(str);
            debugPort.Write(debugBuffer, 0, debugBuffer.Length);
        }

        public void WirelessReceiveThread()
        {
            byte[] readBuffer = new byte[maxTelegram];
            Boolean gotHeader;
            Byte porg = 0;
            int dataLength;
            byte optionalLength;
            PacketType packetType;
            byte crc8h;
            byte crc8d;

            while (true)
            {
                while(wirelessPort.BytesToRead == 0)
                {
                    Thread.Sleep(197);
                }
                do
                {
                    byte[] header = new byte[4];
                    gotHeader = false;
                    do
                    {
                        wirelessPort.Read(readBuffer, 0, 1);
                    }
                    while (readBuffer[0] != SYNCBYTE);

                    wirelessPort.Read(header, 0, 4);

                    dataLength = header[0] << 8 | header[1];
                    optionalLength = header[2];
                    packetType = (PacketType)header[3];

                    wirelessPort.Read(readBuffer, 0, 1);
                    crc8h = readBuffer[0];

                    if (dataLength + optionalLength + 1 >= maxTelegram)
                    {
                        DebugPrint("Large data!\r\n");
                        continue;
                    }
                    gotHeader = crc8h == CRC.Crc8(header);
                }
                while (!gotHeader);

                //DebugPrint("Got Header...\r\n"); //?????????????????

                for (int i = 0; i < dataLength; i++)
                {
                    wirelessPort.Read(readBuffer, i, 1);
                }

                if (packetType == PacketType.Radio || packetType == PacketType.RadioSubTel)
                {
                    // Check CRC
                    if (optionalLength > 0)
                    {
                        wirelessPort.Read(readBuffer, dataLength, optionalLength);
                    }
                    wirelessPort.Read(readBuffer, dataLength + optionalLength, 1);
                    crc8d = readBuffer[dataLength + optionalLength];

                    if (crc8d != CRC.Crc8(readBuffer, dataLength + optionalLength))
                    {
                        DebugPrint("CRC error!\r\n");
                        return;
                    }

                    porg = readBuffer[0];
                    if (porg == porgSYS_EX)
                    {
                        ProcessSYSEX(readBuffer, dataLength);
                    }
                    else if (porg == porgRPS) // RPS or 1BS
                    {
                        //////////////////////////////////////////////////
                        // Only for DEBUG message
                        string from = Sx.XnString(ref readBuffer, 2, 4);
                        string to = Sx.XnString(ref readBuffer, Tx.headerRPS[1] + 1, 4);
                        string stat = Sx.X2String(readBuffer[6]);
                        DebugPrint("RPS " + from + " > " +  to + " " + stat + "\r\n");
                        //////////////////////////////////////////////////
                        if (CompareID(myID, readBuffer, Tx.headerRPS[1] + 1))
                        {
                            ProcessRPSTelegram(readBuffer, dataLength);
                        }
                        else
                        {
                            string my = Sx.XnString(ref myID, 0);
                            DebugPrint("CompareID error: " + my + "\r\n");
                        }
                    }
                    else if (porg == porg4BS)
                    {
                        // Process4BSTelegram(readBuffer, dataSize);
                        // DebugPrint("4BS Received...\r\n");
                    }
                    else
                    {
                        DebugPrint("Invalid porg: " + porg + "\r\n");
                        return;
                    }
                }
                else if (packetType == PacketType.Response)
                {
                    ProcessResponse(readBuffer, dataLength);
                }
            }
        }

        private void ProcessRPSTelegram(byte[] readBuffer, int dataLength)
        {
                    /*            +-----------------------------------------------+ */
                    /*            |  7  |  6  |  5  |  4  |  3  |  2  |  1  |  0  | */
                    /*            +-----------------------------------------------+ */
                    /*            |SYNCBYTE1(0xA5)                                | */
                    /*            +-----------------------------------------------+ */
                    /*            |SYNCBYTE0(0x5A)                                | */
                    /*            +-----------------------------------------------+ */
                    /* F6-02-01                                                     */
                    /*            +-----------------------------------------------+ */
                    /*            |HSEQ             |LENGTH                       | */
                    /*            +-----------------------------------------------+ */
                    /*            |ORG(0x05)                                      | */
                    /*            +-----------------------------------------------+ */
                    /* DATA_BYTE3 |R1               |EB   |R2               |SA   | */
                    /*            +-----------------------------------------------+ */
                    /* DATA_BYTE2 |                                               | */
                    /*            +-----------------------------------------------+ */
                    /* DATA_BYTE1 |                                               | */
                    /*            +-----------------------------------------------+ */
                    /* DATA_BYTE0 |                                               | */
                    /*            +-----------------------------------------------+ */
                    /* ID_BYTE3   |ID                                             | */
                    /*            +-----------------------------------------------+ */
                    /* ID_BYTE2   |                                               | */
                    /*            +-----------------------------------------------+ */
                    /* ID_BYTE1   |                                               | */
                    /*            +-----------------------------------------------+ */
                    /* ID_BYTE0   |                                               | */
                    /*            +-----------------------------------------------+ */
                    /* STATUS     |     |     |T21  |NU   |RC                     | */
                    /*            +-----------------------------------------------+ */
                    /*                                                              */
                    /*            +-----------------------------------------------+ */
                    /*            |CHECKSUM                                       | */
                    /*            +-----------------------------------------------+ */

            byte status = readBuffer[dataLength - 1];
            byte nu = (byte)((status >> 4) & 0x01);

            DebugPrint("ProcessRPSTelegram...\r\n"); //?????????????????

            if (nu == 0x01) // RPS, locker switches
            {
                byte[] rpsData = { (byte) (readBuffer[1] >> 4),
                                   (byte) (readBuffer[1] & 0x0F) };
                for(int i = 0; i < 2; i++)
                {
                    switch (rpsData[i])
                    {
                        case 0x01:
                            DebugPrint("Relay ON\r\n");
                            relayPort.Write(true);
                            break;
                        case 0x03:
                            DebugPrint("Relay OFF\r\n");
                            relayPort.Write(false);
                            break;
                    }
                }
            }
        }

        private void ProcessResponse(byte[] readBuffer, int dataLength)
        {
                    /*            +-----------------------------------------------+ */
                    /*            |  7  |  6  |  5  |  4  |  3  |  2  |  1  |  0  | */
                    /*            +-----------------------------------------------+ */
                    /*            |SYNCBYTE1(0xA5)                                | */
                    /*            +-----------------------------------------------+ */
                    /*            |SYNCBYTE0(0x5A)                                | */
                    /*            +-----------------------------------------------+ */
                    /*                                                              */
                    /*            +-----------------------------------------------+ */
                    /*            |HSEQ             |LENGTH                       | */
                    /*            +-----------------------------------------------+ */
                    /*            |ORG                                            | */
                    /*            +-----------------------------------------------+ */
                    /*            |                                               | */
                    /*            +-----------------------------------------------+ */
                    /*            |                                               | */
                    /*            +-----------------------------------------------+ */
                    /*            |                                               | */
                    /*            +-----------------------------------------------+ */
                    /*            |                                               | */
                    /*            +-----------------------------------------------+ */
                    /*            |                                               | */
                    /*            +-----------------------------------------------+ */
                    /*            |                                               | */
                    /*            +-----------------------------------------------+ */
                    /*            |                                               | */
                    /*            +-----------------------------------------------+ */
                    /*            |                                               | */
                    /*            +-----------------------------------------------+ */
                    /*            |                                               | */
                    /*            +-----------------------------------------------+ */
                    /*                                                              */
                    /*            +-----------------------------------------------+ */
                    /*            |CHECKSUM                                       | */
                    /*            +-----------------------------------------------+ */

            ActionStruct a = new ActionStruct();

            //DebugPrint("ProcessResponse...\r\n"); //?????????????????

            if (dataLength > 4)
            {
                Array.Copy(readBuffer, 1, AppVersion, 0, 4);
            }
            if (dataLength > 8)
            {
                Array.Copy(readBuffer, 5, APIVersion, 0, 4);
            }
            if (dataLength > 12)
            {
                Array.Copy(readBuffer, 9, myID, 0, 4);
            }
            if (dataLength > 16)
            {
                Array.Copy(readBuffer, 13, ChipVersion, 0, 4);
            }
            if (dataLength > 32)
            {
                byte[] appDescArray = new byte[16];
                Array.Copy(readBuffer, 13, appDescArray, 0, 16);
            }

            while (responseQueue.Count == 0)
            {
                Thread.Sleep(61);
            }

            ResponseStruct res = (ResponseStruct)responseQueue.Dequeue();
            int resCode = (int)readBuffer[0];
            a.action = Action.NoAction;
            a.parameter = new byte[] { 0 };

            switch (res.r)
            {
                case Transmit.Response.Teachin:
                    a.action = Action.TransmitPower;
                    EnqueueAndSet(a);
                    break;
                case Transmit.Response.GetVersion:
                    a.action = Action.SetVersion;
                    a.parameter = readBuffer;
                    EnqueueAndSet(a);
                    break;
                case Transmit.Response.ReadRepeater:
                    if (resCode == 0)
                    {
                        a.action = Action.TransmitResponse;
                        a.parameter = new byte[] { (byte)((readBuffer[1] | readBuffer[1] << 1) & readBuffer[2]) };
                        a.destination = res.destination;
                        EnqueueAndSet(a);
                    }
                    else
                    {
                        a.action = Action.TransmitError;
                        a.parameter = new byte[] { (byte)(0xE0 | resCode) };
                        a.destination = res.destination;
                        EnqueueAndSet(a);
                    }
                    break;
                case Transmit.Response.WriteRepeater:
                    if (resCode == 0)
                    {
                        a.action = Action.ReadRepeater;
                        a.destination = res.destination;
                        EnqueueAndSet(a);
                    }
                    else
                    {
                        a.action = Action.TransmitError;
                        a.parameter = new byte[] { (byte)(0xF0 | resCode) };
                        a.destination = res.destination;
                        EnqueueAndSet(a);
                    }
                    break;
                //case Transmit.Response.Data4BS:
                //case Transmit.Response.DataRPS:
                //case Transmit.Response.Data1BS:
                //case Transmit.Response.SYS_EX:
                //case Transmit.Response.RemoteManagement:
                default:
                    break;
            }
        }

        private void ProcessSYSEX(byte[] readBuffer, int dataLength)
        {
            ActionStruct a = new ActionStruct();
            byte[] data = new byte[dataLength - 1]; //readBuffer;
            Array.Copy(readBuffer, 1, data, 0, dataLength - 1);
            DebugPrint("ProcessSYSEX...\r\n"); //?????????????????

            uint seq = (uint)data[0] >> 6;
            uint idx = (uint)data[0] & 0x3F;
            uint data_length = (uint)data[1] << 1 | ((uint)data[2] >> 7);
            uint manufacture_ID = (((uint)data[2]) & 0x7F) << 4 | ((uint)data[3] >> 4);
            uint fn_number = ((uint)data[3] & 0x0F) << 8 | data[4];
            uint Data = data[5];
            byte[] fromId = new byte[4];
            Array.Copy(data, 9, fromId, 0, 4);
            byte[] destId = new byte[4];
            Array.Copy(readBuffer, dataLength + 1, destId, 0, 4);

            if ((RMC)fn_number == RMC.QueryID || (RMC)fn_number == RMC.Ping)
            {
                a.action = Action.AnswerRMC;
                a.destination = fromId;
                a.parameter = new byte[] { (byte)fn_number };
                EnqueueAndSet(a);
            }
            else if (manufacture_ID == manufactureID && (RMC)fn_number == RMC.RPCRepeater && destId != Tx.broadcast)
            {
                switch (Data)
                {
                    case 0x0A:
                        a.action = Action.ReadRepeater;
                        a.destination = fromId;
                        a.parameter = new byte[] { 0 };
                        EnqueueAndSet(a);
                        break;
                    case 0x00:
                    case 0x01:
                    case 0x02:
                        a.action = Action.WriteRepeater;
                        a.destination = fromId;
                        a.parameter = new byte[] { (byte)Data };
                        EnqueueAndSet(a);
                        break;
                    default:
                        a.action = Action.TransmitError;
                        a.destination = fromId;
                        a.parameter = new byte[] { (byte) 0xE0 };
                        EnqueueAndSet(a);
                        break;
                }
            }
        }

        //
        //
        //
        public void WirelessDataSendThread()
        {
            Byte[] outlet1WidebandWatts = System.Text.UTF8Encoding.UTF8.GetBytes(")27$\r");
            Byte[] outlet1WidebandEnergy = System.Text.UTF8Encoding.UTF8.GetBytes(")28$\r");
            Byte[] readBuffer = new Byte[2];
            Byte length;
            String data;
            Byte crc, calccrc;

            while (true)
            {
                powerPort.Write(outlet1WidebandWatts, 0, outlet1WidebandWatts.Length);

                /* )28$<CR><LF>LLDDDDDDDDCC<SP><CR><LF> */
                do
                {
                    powerPort.Read(readBuffer, 0, 1);
                }
                while (readBuffer[0] != LFCODE);

                powerPort.Read(readBuffer, 0, 2);
                length = (Byte)(Convert.ToInt32("" + Convert.ToChar(readBuffer[0]) + Convert.ToChar(readBuffer[1]), 16));

                data = "";
                calccrc = 0xFF;
                for (int i = 0; i < length; i++)
                {
                    powerPort.Read(readBuffer, 0, 1);
                    data = data + Convert.ToChar(readBuffer[0]);
                    calccrc = (Byte)(calccrc ^ readBuffer[0]);
                }
                lastPower = Convert.ToInt32(data, 16);  /* 符号付き32bit前提 */

                powerPort.Read(readBuffer, 0, 2);
                crc = (Byte)(Convert.ToInt32("" + Convert.ToChar(readBuffer[0]) + Convert.ToChar(readBuffer[1]), 16));

                do
                {
                    powerPort.Read(readBuffer, 0, 1);
                }
                while (readBuffer[0] != LFCODE);

                if (calccrc == crc)
                {
                    DebugPrint("TX: " + lastPower.ToString() + "\r\n");

                    TransmitTeachIn();
                }

                Thread.Sleep(60*1000);
            }
        }

        //
        //
        //
        public void ActionThread()
        {
            Random random = new Random(456);
            while (true)
            {
                actionEvent.WaitOne();
                if (actionQueue.Count == 0)
                {
                    Thread.Sleep(33);
                    continue;
                }
                ActionStruct a = (ActionStruct)actionQueue.Dequeue();
                //DebugPrint("Action: " + a.action.ToString() + "\r\n"); //??????????????????????????
                switch (a.action)
                {
                    case Action.TransmitPower:
                        TransmitPower();
                        break;
                    case Action.SetVersion:
                        SetVersion(a.parameter);
                        break;
                    case Action.TransmitResponse:
                        TransmitResponse(a.parameter[0], a.destination);
                        break;
                    case Action.ReadRepeater:
                        ReadRepeater(a.destination);
                        break;
                    case Action.WriteRepeater:
                        WriteRepeater(a.parameter[0], a.destination);
                        break;
                    case Action.AnswerRMC:
                        AnswerRMC(a.parameter[0], a.destination);
                        break;
                    case Action.TransmitError:
                        TransmitError(a.parameter[0], a.destination);
                        break;
                    default:
                        break;
                }
                System.Threading.Thread.Sleep(random.Next(50) + 5); // 5 - 50msec
            }
        }

        private void EnqueueAndSet(ActionStruct a)
        {
            actionQueue.Enqueue(a);
            actionEvent.Set();
        }

        //
        // transmit support
        //
        private void TransmitRMC(int fn_number)
        {
            byte[] data = new byte[0];
            TransmitRMC(fn_number, data);
        }

        private void TransmitRMC(byte[] destination, int fn_number)
        {
            byte[] data = new byte[0];
            TransmitRMC(fn_number, data, destination);
        }

        private void TransmitRMC(int fn_number, byte[] data)
        {
            TransmitRMC(fn_number, data, Tx.broadcast);
        }

        private void TransmitRMC(int fn_number, byte[] data, byte[] destination)
        {
            ResponseStruct res = new ResponseStruct();
            byte[] command = new byte[14];

            byte seq = (byte)(mSeq + 1);
            mSeq = (mSeq + 1) % 3;
            byte idx = 0;
            int data_length = data.Length;

            command[0] = (byte)(seq << 6 | idx);
            command[1] = (byte)(data_length >> 1);
            command[2] = (byte)((data_length & 0x01) << 7 | manufactureID >> 4);
            command[3] = (byte)((manufactureID & 0x0F) << 4 | fn_number >> 8);
            command[4] = (byte)(fn_number & 0xFF); // data
            for (int i = 0; i < data_length; i++)
            {
                command[5 + i] = data[i];
            }
            res.r = (Transmit.Response)porgSYS_EX;
            res.destination = destination;
            //sysexTransmit(command, destination);
            tx.TransmitSYSEX(res, command, destination);
        }

        private void TransmitRPC(int code, int data, byte[] destination)
        {
            TransmitRMC(code, new byte[1] { (byte)data }, destination);
        }

        //
        // Actions for Responses and Received Telegrams
        //
        private void TransmitTeachIn()
        {
            ResponseStruct res = new ResponseStruct();
            byte[] data = new byte[4];
            data[0] = (byte)(eep21Func << 2);
            data[1] = (byte)(eep21Type << 3);
            data[2] = (byte)manufactureID;
            data[3] = 0x80; // telegram with EEP and Man ID
            res.r = (Transmit.Response)data[0];
            res.destination = Tx.broadcast;
            tx.TransmitData(res, porg4BS, data, 0);
        }

        private void TransmitPower()
        {
            int p = lastPower / 1000;
            if (1000 < p)
                p = 1000;
            else if (p < 0)
                p = 0;
            byte pwru = (byte)(p / 10);
            const Byte tmpd = 0;
            const Byte spwru = 0;
            const Byte tmos = 0;
            const Byte drl = 0;
            const Byte lrnb = 1;
            const Byte rsd = 0;
            const Byte red = 0;
            const Byte mpwru = 0;

            //DebugPrint("TransmitPower...\r\n"); //???????????????????????????

            ResponseStruct res = new ResponseStruct();
            byte[] data = {
                              (Byte)tmpd,
                              (Byte)((spwru << 7) | pwru),
                              (Byte)tmos,
                              (Byte)((drl << 4) | (lrnb << 3) | (rsd << 2) | (red << 1) | mpwru)
                          };
            res.r = (Transmit.Response)porg4BS;
            res.destination = Tx.broadcast;
            tx.TransmitData(res, porg4BS, data, 0);
        }

        private void SetVersion(byte[] data)
        {
            DebugPrint("SetVersion Length " + data.Length + "\r\n"); //???????????????????????????

            if (data.Length > 4)
            {
                Array.Copy(data, 1, AppVersion, 0, 4);
            }
            if (data.Length > 8)
            {
                Array.Copy(data, 5, APIVersion, 0, 4);
            }
            if (data.Length > 12)
            {
                Array.Copy(data, 9, myID, 0, 4);
                string my = Sx.XnString(ref myID, 4);
                DebugPrint("MyID " + my + "\r\n");
            }
            if (data.Length > 16)
            {
                Array.Copy(data, 13, ChipVersion, 0, 4);
            }
        }

        private void ReadRepeater(byte[] destination)
        {
            DebugPrint("ReadRepeater...\r\n"); //???????????????????????????

            ResponseStruct res = new ResponseStruct();
            byte[] command = { 0x0A }; // CO_RD_REPEATER
            res.r = (Transmit.Response)command[0];
            res.destination = destination;
            tx.TransmitCommand(res, command);
        }

        private void WriteRepeater(int data, byte[] destination)
        {
            DebugPrint("WriteRepeater...\r\n"); //???????????????????????????

            ResponseStruct res = new ResponseStruct();
            byte[] command = new byte[3];
            command[0] = 0x09; // CO_WR_REPEATER
            command[1] = (byte)(data == 0 ? 0 : 1); // ON or OFF
            command[2] = (byte)data; // data 0, 1, 2
            res.r = (Transmit.Response)command[0];
            res.destination = destination;
            tx.TransmitCommand(res, command);
        }

        private void TransmitResponse(int data, byte[] destination)
        {
            DebugPrint("TransmitResponse...\r\n"); //???????????????????????????
            TransmitRPC((int)RMC.RPCRepeaterAnswer, data, destination);
        }

        private void AnswerRMC(int fn, byte[] destination)
        {
            DebugPrint("AnswerRMC...\r\n"); //???????????????????????????
            RMC fn_number = 0;
            byte[] data = new byte[4];
            data[0] = porg4BS;
            data[1] = eep21Func << 2;
            data[2] = eep21Type << 3;
            data[3] = 0;

            switch ((RMC)fn)
            {
                case RMC.QueryID:
                    fn_number = RMC.QueryIDAnswer;
                    TransmitRMC((int)fn_number, data, destination);
                    break;
                case RMC.Ping:
                    fn_number = RMC.PingAnswer;
                    TransmitRMC((int)fn_number, data, destination);
                    break;
                default:
                    break;
            }
        }

        private void TransmitError(int error, byte[] destination)
        {
            DebugPrint("TransmitError...\r\n"); //???????????????????????????
            TransmitRPC((int)RMC.RPCRepeaterAnswer, error, destination);
        }

        private bool CompareID(byte[] myId, byte[] targetId, int targetStartIndex)
        {
            const int IdLength = 4;
            for (int i = 0; i < IdLength; i++)
            {
                if (myId[i] != targetId[i + targetStartIndex])
                {
                    return false;
                }
            }
            return true;
        }
    }

    ///
    /// Transmit Class
    ///
    public class Transmit
    {
        private static SerialPort serialPort;
        private static Queue responseQueue;
        private static AutoResetEvent sendEvent = new AutoResetEvent(false);
        private static Queue sendQueue = new Queue();
        private static int sendOK = 1;
        private const int BufferSize = 64;
        public const byte headerLength = 6;
        public const byte optionalDataLength = 7;
        //public const byte remanDataLength = 4;
        //public const byte remanOptionalLength = 10;

        public static readonly byte[] broadcast = new byte[4] { 0xFF, 0xFF, 0xFF, 0xFF };

        public static readonly byte[] headerRPS = new byte[]
        {
                0,   // Data Length (higher)
                7,   // Fixed Data Length
                7,   // Fixed Optional Length
                0x01 //Packet Type:Radio
        };

        public static readonly byte[] header4BS = new byte[]
        {
                0,   // Data Length (higher)
                10,  // Fixed Data Length
                7,   // Fixed Optional Length
                0x01 //Packet Type:Radio
        };

        public static readonly byte[] headerSYS_EX = new byte[]
        {
                0,   // Data Length (higher)
                15,  // Fixed Data Length
                7,   // Fixed Optional Length
                0x01 //Packet Type:Radio
        };

        public static readonly byte[] headerCommand = new byte[]
        {
                0,   // Data Length (higher)
                1,   // Data Length (can be modified)
                0,   // Fixed Optional Length
                0x05 //Packet Type:Common_Command
        };
#if false
        public static readonly byte[] headerReMan = new byte[]
        {
                0,   // Data Length (higher)
                4,   // Data Length (4 + read data)
                10,  // Fixed Optional Length
                0x07 //Packet Type:Remote_Man
        };
#endif
        public enum Response
        {
            Teachin = Main.eep21Func << 2, // Data (4BS)
            DataRPS = Main.porgRPS,        // Data
            Data1BS = Main.porg1BS,        // Data
            Data4BS = Main.porg4BS,        // Data
            SYS_EX = Main.porgSYS_EX,    // Data
            GetVersion = 0x03,           // CO
            WriteRepeater = 0x09,        // CO
            ReadRepeater = 0x0A,         // CO
            //RemoteManagement = Main.porgReMan // RM: may be not used 
        };

        public Transmit(SerialPort sp, Queue rp)
        {
            serialPort = sp;
            responseQueue = rp;
        }

        private byte[] GetBuffer(byte porg, int dataLength)
        {
            byte[] writeBuffer = new byte[BufferSize];

            writeBuffer[0] = 0x55;
            for (int i = 5; i < BufferSize; i++)
            {
                writeBuffer[i] = 0;
            }

            switch (porg)
            {
                case Main.porgRPS:
                case Main.porg1BS:
                    headerRPS.CopyTo(writeBuffer, 1);
                    break;
                case Main.porg4BS:
                    header4BS.CopyTo(writeBuffer, 1);
                    break;
                case Main.porgSYS_EX:
                    headerSYS_EX.CopyTo(writeBuffer, 1);
                    break;
                //case Main.porgReMan:
                //    headerReMan.CopyTo(writeBuffer, 1);
                //    break;
                default:
                    headerCommand.CopyTo(writeBuffer, 1);
                    if (optionalDataLength > 1)
                    {
                        writeBuffer[2] = (byte)dataLength;
                    }
                    break;
            }
            writeBuffer[5] = CRC.Crc8(writeBuffer, 1, 4);
            return writeBuffer;
        }

        public void TransmitCommand(object response, byte[] buffer)
        {
            byte dataLength = (byte)buffer.Length;
            byte[] wBuffer = GetBuffer(buffer[0], dataLength);

            buffer.CopyTo(wBuffer, 6);
            wBuffer[headerLength + dataLength] = CRC.Crc8(buffer);

            InterlockedTransmit(response,
                wBuffer, headerLength + dataLength + 1);

            return;
        }

        public void TransmitData(object response, byte porg, byte[] data, byte status)
        {

            byte[] wBuffer = GetBuffer(porg, 0);
            byte dataLength = wBuffer[2];
            byte[] allData = new byte[dataLength];

            allData[0] = porg;
            for (int i = 1; i < dataLength; i++)
            {
                allData[i] = 0;
            }
            Array.Copy(data, 0, allData, 1, dataLength - 6); // Copy User Data
            allData[dataLength - 1] = status;
            allData.CopyTo(wBuffer, headerLength);

            // OptionalData
            byte[] optionalData;
            OptionalData(out optionalData, broadcast);

            optionalData.CopyTo(wBuffer, headerLength + dataLength);
            wBuffer[headerLength + dataLength + optionalDataLength]
                = CRC.Crc8(wBuffer, headerLength, dataLength + optionalDataLength);

            InterlockedTransmit(response,
                wBuffer, headerLength + dataLength + optionalDataLength + 1);

            return;
        }

        public void TransmitSYSEX(object response, byte[] data, byte[] destination)
        {
            byte[] wBuffer = GetBuffer(Main.porgSYS_EX, 0);
            byte dataLength = headerSYS_EX[1];
            byte[] allData = new byte[dataLength];
            allData[0] = Main.porgSYS_EX;
            for (int i = 1; i < dataLength; i++)
            {
                allData[i] = 0;
            }
            Array.Copy(data, 0, allData, 1, dataLength - 6); // Copy User Data
            allData[dataLength - 1] = 0x8F; // Unknown Status
            allData.CopyTo(wBuffer, headerLength);

            // OptionalData
            byte[] optionalData;
            OptionalData(out optionalData, destination);

            optionalData.CopyTo(wBuffer, headerLength + dataLength);
            wBuffer[headerLength + dataLength + optionalDataLength]
                = CRC.Crc8(wBuffer, headerLength, dataLength + optionalDataLength);

            InterlockedTransmit(response,
                wBuffer, headerLength + dataLength + optionalDataLength + 1);

            return;
        }

#if false
        public void TransmitReMan(object response, byte[] data)
        {
            int dataBufferLength = data.Length + remanOptionalLength;
            byte[] wBuffer = GetBuffer(Main.porgReMan, dataBufferLength);
            data.CopyTo(wBuffer, headerLength);

            // Optional data 0..3: Destination ID
            broadcast.CopyTo(wBuffer, headerLength + data.Length);

            // Optional data 4..7: Source ID
            // writeBuffer[4..7 + dataLength] = 0;
            wBuffer[headerLength + data.Length + 8] = 0xFF; // dBm
            wBuffer[headerLength + data.Length + 9] = 0;    // Send With Delay
            wBuffer[headerLength + data.Length + remanOptionalLength]
                = CRC.Crc8(wBuffer, headerLength, data.Length + remanOptionalLength);

            InterlockedTransmit(response, wBuffer,
                headerLength + data.Length + remanOptionalLength + 1);

            return;
        }
#endif

        private void OptionalData(out byte[] data, byte[] destination)
        {
            data = new byte[optionalDataLength];
            data[0] = 0x03; // Send Telegram
            for (int i = 0; i < 4; i++)
            {
                data[1 + i] = destination[i];
            }
            data[5] = 0xFF; //dBm;
            data[6] = 0; // Security Level
        }

        private void InterlockedTransmit(object response, byte[] buffer, int length)
        {
            while (Interlocked.CompareExchange(ref sendOK, 1, 0) == 0)
            {
                sendQueue.Enqueue(0);
                sendEvent.WaitOne();
                sendQueue.Dequeue();
            }

            responseQueue.Enqueue(response);

            serialPort.Write(buffer, 0, length);
            sendOK = 1;
            if (sendQueue.Count > 0)
            {
                sendEvent.Set();
            }
        }
    }

    //
    // CRC Class
    //
    public static class CRC
    {
        private static Byte[] crc8Table = new Byte[]
        {
            0x00, 0x07, 0x0e, 0x09, 0x1c, 0x1b, 0x12, 0x15,
            0x38, 0x3f, 0x36, 0x31, 0x24, 0x23, 0x2a, 0x2d,
            0x70, 0x77, 0x7e, 0x79, 0x6c, 0x6b, 0x62, 0x65,
            0x48, 0x4f, 0x46, 0x41, 0x54, 0x53, 0x5a, 0x5d,
            0xe0, 0xe7, 0xee, 0xe9, 0xfc, 0xfb, 0xf2, 0xf5,
            0xd8, 0xdf, 0xd6, 0xd1, 0xc4, 0xc3, 0xca, 0xcd,
            0x90, 0x97, 0x9e, 0x99, 0x8c, 0x8b, 0x82, 0x85,
            0xa8, 0xaf, 0xa6, 0xa1, 0xb4, 0xb3, 0xba, 0xbd,
            0xc7, 0xc0, 0xc9, 0xce, 0xdb, 0xdc, 0xd5, 0xd2,
            0xff, 0xf8, 0xf1, 0xf6, 0xe3, 0xe4, 0xed, 0xea,
            0xb7, 0xb0, 0xb9, 0xbe, 0xab, 0xac, 0xa5, 0xa2,
            0x8f, 0x88, 0x81, 0x86, 0x93, 0x94, 0x9d, 0x9a,
            0x27, 0x20, 0x29, 0x2e, 0x3b, 0x3c, 0x35, 0x32,
            0x1f, 0x18, 0x11, 0x16, 0x03, 0x04, 0x0d, 0x0a,
            0x57, 0x50, 0x59, 0x5e, 0x4b, 0x4c, 0x45, 0x42,
            0x6f, 0x68, 0x61, 0x66, 0x73, 0x74, 0x7d, 0x7a,
            0x89, 0x8e, 0x87, 0x80, 0x95, 0x92, 0x9b, 0x9c,
            0xb1, 0xb6, 0xbf, 0xb8, 0xad, 0xaa, 0xa3, 0xa4,
            0xf9, 0xfe, 0xf7, 0xf0, 0xe5, 0xe2, 0xeb, 0xec,
            0xc1, 0xc6, 0xcf, 0xc8, 0xdd, 0xda, 0xd3, 0xd4,
            0x69, 0x6e, 0x67, 0x60, 0x75, 0x72, 0x7b, 0x7c,
            0x51, 0x56, 0x5f, 0x58, 0x4d, 0x4a, 0x43, 0x44,
            0x19, 0x1e, 0x17, 0x10, 0x05, 0x02, 0x0b, 0x0c,
            0x21, 0x26, 0x2f, 0x28, 0x3d, 0x3a, 0x33, 0x34,
            0x4e, 0x49, 0x40, 0x47, 0x52, 0x55, 0x5c, 0x5b,
            0x76, 0x71, 0x78, 0x7f, 0x6A, 0x6d, 0x64, 0x63,
            0x3e, 0x39, 0x30, 0x37, 0x22, 0x25, 0x2c, 0x2b,
            0x06, 0x01, 0x08, 0x0f, 0x1a, 0x1d, 0x14, 0x13,
            0xae, 0xa9, 0xa0, 0xa7, 0xb2, 0xb5, 0xbc, 0xbb,
            0x96, 0x91, 0x98, 0x9f, 0x8a, 0x8D, 0x84, 0x83,
            0xde, 0xd9, 0xd0, 0xd7, 0xc2, 0xc5, 0xcc, 0xcb,
            0xe6, 0xe1, 0xe8, 0xef, 0xfa, 0xfd, 0xf4, 0xf3
        };

        public static Byte Crc8(Byte[] data)
        {
            Byte crc = 0;
            for (int i = 0; i < data.Length; i++)
                crc = crc8Table[crc ^ data[i]];
            return crc;
        }

        public static Byte Crc8(Byte[] data, int count)
        {
            Byte crc = 0;
            for (int i = 0; i < count; i++)
                crc = crc8Table[crc ^ data[i]];
            return crc;
        }

        public static Byte Crc8(Byte[] data, int start, int count)
        {
            Byte crc = 0;
            count += start;
            for (int i = start; i < count; i++)
                crc = crc8Table[crc ^ data[i]];
            return crc;
        }
    }

    public static class StringExtensions
    {
        private static readonly string[] hexes = new string[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "A", "B", "C", "D", "E", "F", };
        public static string X2String(byte b)
        {
            string s = hexes[(b >> 4)];
            s += hexes[b & 0x0F];
            return s;
        }

        public static string XnString(ref byte[] b, int start, int length)
        {
            string s = "";
            for (int i = 0; i < length; i++)
            {
                s += X2String(b[i + start]);
            }
            return s;
        }

        public static string XnString(ref byte[] b, int length)
        {
            return XnString(ref b, 0, length); 
        }

        public static string XnString(ref byte[] b)
        {
            return XnString(ref b, b.Length);
        }
    }
}
