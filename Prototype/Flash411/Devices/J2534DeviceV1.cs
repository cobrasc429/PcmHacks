﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using J2534;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace Flash411
{
    /// <summary>
    /// This class encapsulates all code that is unique to the AVT 852 interface.
    /// </summary>
    /// 
    class J2534DeviceV1 : Device
    {
        /// <summary>
        /// Configuration settings
        /// </summary>
        public int ReadTimeout = 3000;
        public int WriteTimeout = 1000;


        /// <summary>
        /// variety of properties used to id channels, fitlers and status
        /// </summary>
        private new J2534_Struct J2534Port;
        public string Firmware;
        public string DLL;
        public string API;
        public
        new List<ulong> PeriodicMsgs;
        new List<ulong> Filters;
        private int DeviceID;
        private int ChannelID;
        private ProtocolID Protocol;
        public bool IsProtocolOpen;
        public bool IsJ2534Open;
        new public List<J2534Device> InstalledDLLs;
        private const string PortName = "J2534";
        public string ToolName = "";

        /// <summary>
        /// global error variable for reading/writing. (Could be done on the fly)
        /// TODO, keep record of all errors for debug
        /// </summary>
        public J2534Err OBDError;

        /// <summary>
        /// J2534 has two parts.
        /// J2534device which has the supported protocols ect as indicated by dll and registry.
        /// J2534extended which is al the actual commands and functions to be used. 
        /// </summary>
        struct J2534_Struct
        {
            public J2534Extended Functions;
            public J2534Device LoadedDevice;
        }
     
        public J2534DeviceV1(J2534Device jport, ILogger logger) : base(logger)
        {
          
            J2534Port = new J2534_Struct();
            J2534Port.Functions = new J2534Extended();
            J2534Port.LoadedDevice = new J2534Device();
            J2534Port.LoadedDevice = jport;
        }

        public override string ToString()
        {
            return "J2534 Device V1";
        }

        public override async Task<bool> Initialize()
        {
            Filters = new List<ulong>();

            this.Logger.AddDebugMessage("Initialize called");
            this.Logger.AddDebugMessage("Initializing " + this.ToString());

            Response<J2534Err> m; // hold returned messages for processing
            Response<bool> m2;
            Response<double> volts;

            //Check J2534 API
            //this.Logger.AddDebugMessage(J2534Port.Functions.ToString());


            //Check not already loaded
            if (IsLoaded == true)
            {
                this.Logger.AddDebugMessage("DLL already loaded, unloading before proceeding");
                m2 = await CloseLibrary();
                if (m2.Status != ResponseStatus.Success)
                {
                    this.Logger.AddDebugMessage("Error closing loaded DLL");
                    return false;
                }
                this.Logger.AddDebugMessage("DLL successfully unloaded");
            }

            //Connect to requested DLL
            m2 = await LoadLibrary(J2534Port.LoadedDevice);
            if (m2.Status != ResponseStatus.Success)
            {
                this.Logger.AddDebugMessage("Error occured loading J2534 DLL");
                return false;
            }
            this.Logger.AddUserMessage("Loaded DLL");


            //connect to scantool
            m = await ConnectTool();
            if (m.Status != ResponseStatus.Success)
            {
                this.Logger.AddDebugMessage("Error occured connecting to scantool");
                return false;
            }
            this.Logger.AddUserMessage("Connected to Scantool");


            //Optional.. read API,firmware version ect





            //read voltage
            volts = await ReadVoltage();
            if (volts.Status != ResponseStatus.Success)
            {
                this.Logger.AddDebugMessage("Error occured reading voltage");
                return false;
            }
            this.Logger.AddUserMessage("Battery Voltage is: " + volts.Value.ToString());
            this.Logger.AddDebugMessage("Battery Voltage is: " + volts.Value.ToString());


            //Set Protocol
            m = await ConnectToProtocol(ProtocolID.J1850VPW, BaudRate.J1850VPW_10400, ConnectFlag.NONE);
            if (m.Status != ResponseStatus.Success)
            {
                this.Logger.AddDebugMessage("Failed to set protocol, J2534 error code: 0x" + m.Value.ToString("X2"));
                return false;
            }
            this.Logger.AddUserMessage("Protocol Set");



            //Set filter
            m = await SetFilter(0xFFFFFF, 0x6CF010, 0, TxFlag.NONE, FilterType.PASS_FILTER);
            if (m.Status != ResponseStatus.Success)
            {
                this.Logger.AddDebugMessage("Failed to set filter, J2534 error code: 0x" + m.Value.ToString("X2"));
                return false;
            }
            this.Logger.AddUserMessage("Filter Set");

            //filter now set.. network messages can now be sent/received.


            //finished!
            return true;
        }

        /// <summary>
        /// This will process incoming messages for up to 500ms looking for a message
        /// </summary>
        public async Task<Response<Message>> FindResponse(Message expected)
        {
            return null;
        }

        /// <summary>
        /// Read an network packet from the interface, and return a Response/Message
        /// </summary>
        async private Task<Response<Message>> ReadNetworkMessage()
        {
            PassThruMsg PassMess = new PassThruMsg();
            Message TempMessage = new Message(null);
            int NumMessages = 1;
            IntPtr rxMsgs = Marshal.AllocHGlobal((int)(Marshal.SizeOf(typeof(PassThruMsg)) * NumMessages));

            this.Logger.AddDebugMessage("Trace: Read Network Packet");

            //Clear any previous faults
            OBDError = 0;

            Stopwatch sw = new Stopwatch();
            sw.Start();

            while (OBDError == J2534Err.STATUS_NOERROR || sw.ElapsedMilliseconds > (long)ReadTimeout)
            {
                NumMessages = 1;
                OBDError = J2534Port.Functions.PassThruReadMsgs(ChannelID, rxMsgs, ref NumMessages, ReadTimeout);
                if (OBDError != J2534Err.STATUS_NOERROR) { return Response.Create(ResponseStatus.Error, new Message(null, 0, (ulong)OBDError)); }
                sw.Stop();
               PassMess = rxMsgs.AsMsgList(1).Last();
                if ((int)PassMess.RxStatus == (((int)RxStatus.TX_INDICATION_SUCCESS) + ((int)RxStatus.TX_MSG_TYPE)) || (PassMess.RxStatus == RxStatus.START_OF_MESSAGE)) continue;
                else
                {
                    byte[] TempBytes = PassMess.GetBytes();
                    //Perform additional filter check if required here... or show to debug
                    break;//leave while loop
                }
            }

            if (OBDError != J2534Err.STATUS_NOERROR || sw.ElapsedMilliseconds > (long)ReadTimeout) return Response.Create(ResponseStatus.Error, new Message(null, 0, (ulong)OBDError));

            return Response.Create(ResponseStatus.Success, new Message(PassMess.GetBytes(), PassMess.Timestamp, (ulong)OBDError));
        }

        /// <summary>
        /// Convert a Message to an J2534 formatted transmit, and send to the interface
        /// </summary>
        async private Task<Response<J2534Err>> SendNetworkMessage(Message message, TxFlag Flags)
        {
            this.Logger.AddDebugMessage("Trace: Send Network Packet");

            PassThruMsg TempMsg = new PassThruMsg();
            TempMsg.ProtocolID = Protocol;
            TempMsg.TxFlags = Flags;
            TempMsg.SetBytes(message.GetBytes());

            int NumMsgs = 1;
            OBDError = J2534Port.Functions.PassThruWriteMsgs(ChannelID, TempMsg.ToIntPtr(), ref NumMsgs, WriteTimeout);
            if (OBDError != J2534Err.STATUS_NOERROR)
            {
                //Debug messages here...check why failed..
                return Response.Create(ResponseStatus.Error, OBDError);
            }
            return Response.Create(ResponseStatus.Success, OBDError);
        }

       
        /// <summary>
        /// Send a message, do not expect a response.
        /// </summary>
        public override Task<bool> SendMessage(Message message)
        {
            this.Logger.AddDebugMessage("Sendmessage called");
            StringBuilder builder = new StringBuilder();
            this.Logger.AddDebugMessage("Sending message " + message.GetBytes().ToHex());
            this.Port.Send(message.GetBytes());
            return Task.FromResult(true);
        }

        /// <summary>
        /// Send a message, wait for a response, return the response.
        /// </summary>
        public override async Task<Response<Message>> SendRequest(Message message)
        {
            this.Logger.AddDebugMessage("Send request called");
            this.Logger.AddUserMessage("TX: " + message.GetBytes().ToHex());
            Response<J2534Err> MyError = await SendNetworkMessage(message,TxFlag.NONE);
            if (MyError.Status != ResponseStatus.Success)
            {
                //error here!!
                return Response.Create(MyError.Status, new Message(null));
            }

            Response<Message> response = await ReadNetworkMessage();
            if (response.Status != ResponseStatus.Success) return response;

            this.Logger.AddDebugMessage("RX: " + response.Value.GetBytes().ToHex());

            return response;
        }






        /// <summary>
        /// load in dll
        /// </summary>
        async private Task<Response<bool>> LoadLibrary(J2534Device TempDevice)
        {
            ToolName = TempDevice.Name;
            J2534Port.LoadedDevice = TempDevice;
            if (J2534Port.Functions.LoadLibrary(J2534Port.LoadedDevice))
            {
                return Response.Create(ResponseStatus.Success, true);
            }
            else
            {
                return Response.Create(ResponseStatus.Error, false);
            }
        }

        /// <summary>
        /// unload dll
        /// </summary>
        async private Task<Response<bool>> CloseLibrary()
        {
            if (J2534Port.Functions.FreeLibrary())
            {
                return Response.Create(ResponseStatus.Success, true);
            }
            else
            {
                return Response.Create(ResponseStatus.Error, false);
            }

        }

        /// <summary>
        /// Connects to physical scantool
        /// </summary>
        async private Task<Response<J2534Err>> ConnectTool()
        {
            DeviceID = 0;
            ChannelID = 0;
            Filters.Clear();
            OBDError = 0;
            OBDError = J2534Port.Functions.PassThruOpen(IntPtr.Zero, ref DeviceID);
            if (OBDError != J2534Err.STATUS_NOERROR)
            {
                IsJ2534Open = false;
                return Response.Create(ResponseStatus.Error, OBDError);
            }            
            else
            {
                IsJ2534Open = true;
                return Response.Create(ResponseStatus.Success, OBDError);
            } 
     
            
        }

        /// <summary>
        /// Disconnects from physical scantool
        /// </summary>
        async private Task<Response<J2534Err>> DisconnectTool()
        {
            OBDError = J2534Port.Functions.PassThruClose(DeviceID);
            if (OBDError != J2534Err.STATUS_NOERROR)
            {
                //big problems, do something here
            }
            IsJ2534Open = false;
            return Response.Create(ResponseStatus.Success, OBDError);
        }

        /// <summary>
        /// keep record if DLL has been loaded in
        /// </summary>
        public bool IsLoaded
        {
            get { return J2534Port.Functions.IsLoaded; }
            set {; }
        }

        /// <summary>
        /// connect to selected protocol
        /// Must provide protocol, speed, connection flags, recommended optional is pins
        /// </summary>
        async private Task<Response<J2534Err>>  ConnectToProtocol(ProtocolID ReqProtocol, BaudRate Speed, ConnectFlag ConnectFlags)
        {
            OBDError = J2534Port.Functions.PassThruConnect(DeviceID, ReqProtocol, ConnectFlags, Speed, ref ChannelID);
            if (OBDError != J2534Err.STATUS_NOERROR) return Response.Create(ResponseStatus.Error, OBDError);
            Protocol = ReqProtocol;
            IsProtocolOpen = true;
            return Response.Create(ResponseStatus.Success, OBDError);
        }

        
        /// <summary>
        /// Read battery voltage
        /// </summary>
        async public Task<Response<double>> ReadVoltage()
        {
            double Volts = 0;
            int VoltsAsInt = 0;
            OBDError = J2534Port.Functions.ReadBatteryVoltage(DeviceID, ref VoltsAsInt);
            if (OBDError != J2534Err.STATUS_NOERROR)
            {
                return Response.Create(ResponseStatus.Error, Volts);
            }
            else
            {
                Volts = VoltsAsInt / 1000.0;
                return Response.Create(ResponseStatus.Success, Volts);
            }
           
        }


        /// <summary>
        /// Set filter
        /// </summary>
        async private Task<Response<J2534Err>> SetFilter(UInt32 Mask,UInt32 Pattern,UInt32 FlowControl,TxFlag txflag,FilterType Filtertype)
        {
            PassThruMsg maskMsg;
            PassThruMsg patternMsg;
            PassThruMsg flowControlMsg;

            IntPtr MaskPtr;
            IntPtr PatternPtr;
            IntPtr FlowPtr;

            //byte[] tempbytes = { (byte)(0xFF & (Mask >> 16)), (byte)(0xFF & (Mask >> 8)), (byte)(0xFF & Mask) };

            maskMsg = new PassThruMsg(Protocol, txflag, new Byte[] { (byte)(0xFF & (Mask >> 16)), (byte)(0xFF & (Mask >> 8)), (byte)(0xFF & Mask) });
            patternMsg = new PassThruMsg(Protocol, txflag, new Byte[] { (byte)(0xFF & (Pattern >> 16)), (byte)(0xFF & (Pattern >> 8)), (byte)(0xFF & Pattern) });
            MaskPtr = maskMsg.ToIntPtr();
            PatternPtr = patternMsg.ToIntPtr();
            FlowPtr = IntPtr.Zero;

            int tempfilter = 0;
            OBDError = J2534Port.Functions.PassThruStartMsgFilter(ChannelID, Filtertype, MaskPtr, PatternPtr, FlowPtr, ref tempfilter);
            if (OBDError != J2534Err.STATUS_NOERROR) return Response.Create(ResponseStatus.Error, OBDError);
            Filters.Add((ulong)tempfilter);
            return Response.Create(ResponseStatus.Success, OBDError);
        }





        /// <summary>
        /// Find all installed J2534 DLLs
        /// </summary>
        //private const string PASSTHRU_REGISTRY_PATH = "Software\\PassThruSupport.04.04";
        //private const string PASSTHRU_REGISTRY_PATH_6432 = "Software\\Wow6432Node\\PassThruSupport.04.04";
        //public bool FindInstalledJ2534DLLs()
        //{
        //    try
        //    {
        //        InstalledDLLs = new List<J2534Device>();
        //        RegistryKey myKey = Registry.LocalMachine.OpenSubKey(PASSTHRU_REGISTRY_PATH, false);
        //        if ((myKey == null))
        //        {
        //            myKey = Registry.LocalMachine.OpenSubKey(PASSTHRU_REGISTRY_PATH_6432, false);
        //            if ((myKey == null))
        //            {
        //                return false;
        //            }

        //        }

        //        string[] devices = myKey.GetSubKeyNames();
        //        foreach (string device in devices)
        //        {
        //            J2534Device tempDevice = new J2534Device();
        //            RegistryKey deviceKey = myKey.OpenSubKey(device);
        //            if ((deviceKey == null))
        //            {
        //                continue; //Skip device... its empty
        //            }

        //            tempDevice.Vendor = (string)deviceKey.GetValue("Vendor", "");
        //            tempDevice.Name = (string)deviceKey.GetValue("Name", "");
        //            tempDevice.ConfigApplication = (string)deviceKey.GetValue("ConfigApplication", "");
        //            tempDevice.FunctionLibrary = (string)deviceKey.GetValue("FunctionLibrary", "");
        //            tempDevice.CAN = (int)(deviceKey.GetValue("CAN", 0));
        //            tempDevice.ISO14230 = (int)(deviceKey.GetValue("ISO14230", 0));
        //            tempDevice.ISO15765 = (int)(deviceKey.GetValue("ISO15765", 0));
        //            tempDevice.ISO9141 = (int)(deviceKey.GetValue("ISO9141", 0));
        //            tempDevice.J1850PWM = (int)(deviceKey.GetValue("J1850PWM", 0));
        //            tempDevice.J1850VPW = (int)(deviceKey.GetValue("J1850VPW", 0));
        //            tempDevice.SCI_A_ENGINE = (int)(deviceKey.GetValue("SCI_A_ENGINE", 0));
        //            tempDevice.SCI_A_TRANS = (int)(deviceKey.GetValue("SCI_A_TRANS", 0));
        //            tempDevice.SCI_B_ENGINE = (int)(deviceKey.GetValue("SCI_B_ENGINE", 0));
        //            tempDevice.SCI_B_TRANS = (int)(deviceKey.GetValue("SCI_B_TRANS", 0));
        //            InstalledDLLs.Add(tempDevice);
        //        }
        //        return true;
        //    }
        //    catch (Exception ex)
        //    {
        //        this.Logger.AddDebugMessage("Error occured while finding installed J2534 devices");
        //        //do something with errors here for now return false
        //        return false;
        //    }

        //}

    }
}