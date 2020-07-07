﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Java.Interop;
using Java.IO;

namespace TrojanPlusApp.Droid
{
    [Register("com.trojan_plus.android.TrojanPlusVPNService")]
    [Service(Name = "com.trojan_plus.android.TrojanPlusVPNService",
            Enabled = true,
            Permission = "android.permission.BIND_VPN_SERVICE",
            Process = ":vpn_remote",
            Exported = true)]
    [IntentFilter(new string[] { "android.net.VpnService" })]
    public class TrojanPlusVPNService : Android.Net.VpnService
    {
        private static readonly string TAG = typeof(TrojanPlusVPNService).Name;

        private const string VPN_ADDRESS = "10.0.0.1";
        private const string VPN_ROUTE = "0.0.0.0";
        private const string VPN_DNS_SERVER = "10.254.240.1";
        private const int VPN_MTU = 1500;

        [DllImport("trojan.so", EntryPoint = "Java_com_trojan_1plus_android_TrojanPlusVPNService_runMain")]
        public static extern void RunMain(IntPtr jnienv, IntPtr jclass, IntPtr configPath);

        [DllImport("trojan.so", EntryPoint = "Java_com_trojan_1plus_android_TrojanPlusVPNService_stopMain")]
        public static extern void StopMain(IntPtr jnienv, IntPtr jclass);

        [Export("protectSocket")]
        public static void ProtectSocket(int socket)
        {
            if (sm_currentService != null)
            {
                sm_currentService.Protect(socket);
            }
        }

        static TrojanPlusVPNService sm_currentService = null;
        Messenger messenger;
        private ParcelFileDescriptor m_vpnFD = null;

        private Thread m_worker = null;
        private string m_configPath;
        private Messenger m_replyMessenger = null;

        public override void OnCreate()
        {
            base.OnCreate();
            messenger = new Messenger(new VpnServiceHandler(this));

        }

        public override IBinder OnBind(Intent intent)
        {
            Log.Debug(TAG, "OnBind");
            return messenger.Binder;
        }

        public override void OnDestroy()
        {
            Log.Debug(TAG, "OnDestroy");
            messenger.Dispose();
            sm_currentService = null;

            base.OnDestroy();

            // kill this service to reset memory, otherwise libtrojan.so won't work
            Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
        }

        public override bool OnUnbind(Intent intent)
        {
            OnStartCommand(intent, 0, 0);
            return false;
        }

        [return: GeneratedEnum]
        public override StartCommandResult OnStartCommand(Intent intent, [GeneratedEnum] StartCommandFlags flags, int startId)
        {
            Log.Debug(TAG, "TrojanPlusVPNService.onStartCommand");
            return StartCommandResult.Sticky;
        }

        private void BroadcastStatus(bool started)
        {
            if (m_replyMessenger != null)
            {
                var msg = Message.Obtain(null, TrojanPlusStarter.VPN_START);
                msg.Data = new Bundle();
                msg.Data.PutBoolean("start", started);

                m_replyMessenger.Send(msg);
            }
        }

        private void OpenFD()
        {
            if (m_vpnFD == null)
            {

                PendingIntent pintent = PendingIntent.GetActivity(Application.Context, 0,
                    new Intent(Application.Context, typeof(MainActivity)).SetFlags(ActivityFlags.ReorderToFront), 0);

                Builder builder = new Builder(this);
                builder.AddAddress(VPN_ADDRESS, 32)
                            .AddRoute(VPN_ROUTE, 0)
                            .SetMtu(VPN_MTU)
                            .SetConfigureIntent(pintent)
                            .AddDnsServer(VPN_DNS_SERVER)
                            .SetSession(GetString(Resource.String.app_name));

                m_vpnFD = builder.Establish();
                try
                {
                    var configFile = System.IO.File.ReadAllText(m_configPath)
                                        .Replace("${fd}", m_vpnFD.Fd.ToString());

                    System.IO.File.WriteAllText(m_configPath, configFile);
                }
                catch (Exception ex)
                {
                    Log.Error(TAG, ex.StackTrace);
                    CloseFD();
                    StopSelf();
                    return;
                }

                m_worker = new Thread(new WorkerThread(this).Run);
                m_worker.Start();
            }
        }

        private void CloseFD()
        {
            if (m_vpnFD != null)
            {
                try
                {
                    Log.Debug(TAG, "close fd");
                    m_vpnFD.Close();
                }
                catch (Exception ex)
                {
                    Log.Error(TAG, ex.Message + "\n" + ex.StackTrace);
                }
                m_vpnFD = null;
            }
        }

        private class WorkerThread
        {
            private readonly TrojanPlusVPNService m_service;
            public WorkerThread(TrojanPlusVPNService service)
            {
                m_service = service;
            }

            public void Run()
            {
                IntPtr path = JNIEnv.NewString(m_service.m_configPath);
                try
                {
                    m_service.BroadcastStatus(true);

                    var jclass = JNIEnv.FindClass("com.trojan_plus.android.TrojanPlusVPNService");
                    RunMain(JNIEnv.Handle, jclass, path);
                }
                catch (Exception ex)
                {
                    Log.Error(TAG, ex.Message + "\n" + ex.StackTrace);
                }
                finally
                {
                    JNIEnv.DeleteLocalRef(path);
                }

                m_service.CloseFD();
                m_service.StopSelf();
                m_service.m_worker = null;
                m_service.BroadcastStatus(false);
            }
        }

        private class VpnServiceHandler : Handler
        {
            private readonly TrojanPlusVPNService m_service;
            public VpnServiceHandler(TrojanPlusVPNService service)
            {
                m_service = service;
            }

            public override void HandleMessage(Message msg)
            {
                m_service.m_replyMessenger = msg.ReplyTo;
                switch (msg.What)
                {
                    case TrojanPlusStarter.VPN_START:
                        if (m_service.m_worker == null)
                        {
                            Log.Debug(TAG, "on VpnServiceHandler.HandleMessage VPN_START");
                            m_service.m_configPath = msg.Data.GetString("config");
                            m_service.OpenFD();
                        }
                        break;
                    case TrojanPlusStarter.VPN_STATUS_ASK:
                        {
                            var reply = Message.Obtain(null, TrojanPlusStarter.VPN_STATUS_ASK);
                            reply.Data = new Bundle();
                            reply.Data.PutBoolean("start", m_service.m_worker != null);

                            msg.ReplyTo.Send(reply);
                        }
                        break;
                    case TrojanPlusStarter.VPN_STOP:
                        if (m_service.m_worker != null)
                        {
                            Log.Debug(TAG, "on VpnServiceHandler.HandleMessage VPN_STOP");
                            StopMain(IntPtr.Zero, IntPtr.Zero);
                        }
                        break;
                    default:
                        Log.Warn(TAG, $"Unknown msg.what value: {msg.What} . Ignoring this message.");
                        break;
                }
            }
        }
    }
}