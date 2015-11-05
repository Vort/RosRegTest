using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using Microsoft.Win32;

using SevenZip;
using DiscUtils.Iso9660;
using System.Diagnostics;

namespace RosRegTest
{
    public partial class MainWindow : Window
    {
        private string vboxManagePath;

        private bool InitVBoxManagePath()
        {
            RegistryKey localKey;
            if (Environment.Is64BitOperatingSystem)
                localKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            else
                localKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);

            RegistryKey subKey = localKey.OpenSubKey("SOFTWARE\\Oracle\\VirtualBox");
            if (subKey == null)
                return false;

            object dirObj = subKey.GetValue("InstallDir");
            if (dirObj == null)
                return false;
            if (!(dirObj is string))
                return false;

            vboxManagePath = (dirObj as string) + "VBoxManage.exe";

            if (!File.Exists(vboxManagePath))
                return false;

            return true;
        }

        public MainWindow()
        {
            InitializeComponent();

            if (!InitVBoxManagePath())
            {
                MessageBox.Show("VirtualBox installation not found",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloneCdDirectory(string dir, CDReader cdr, CDBuilder cdb)
        {
            foreach (string fileName in cdr.GetFiles(dir))
            {
                if (fileName == "\\reactos\\unattend.inf")
                    continue;

                var stream = cdr.OpenFile(fileName, FileMode.Open);
                cdb.AddFile(fileName.Remove(0, 1), stream);
                stream.Close();
            }
            foreach (string dirName in cdr.GetDirectories(dir))
            {
                CloneCdDirectory(dirName, cdr, cdb);
            }
        }

        private void Exec(string fileName, string arguments)
        {
            Process proc = new Process();

            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;

            proc.StartInfo.FileName = fileName;
            proc.StartInfo.Arguments = arguments;

            proc.Start();
            proc.WaitForExit();
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            int revision = 0;
            if (!int.TryParse(RevTextBox.Text, out revision))
                return;

            string additionalFileName = null;
            if (AddFileTextBox.Text != "")
                additionalFileName = AddFileTextBox.Text;

            if (additionalFileName != null)
                if (!File.Exists(additionalFileName))
                    return;

            RunButton.IsEnabled = false;

            Task task = new Task(() =>
                {
                    string filename = string.Format("bootcd-{0}-dbg", revision);
                    string filename7z = filename + ".7z";
                    string url = "http://iso.reactos.org/bootcd/" + filename7z;

                    if (!File.Exists(filename7z))
                    {
                        if (File.Exists(filename7z + ".temp"))
                            File.Delete(filename7z + ".temp");

                        WebClient wc = new WebClient();
                        wc.DownloadFile(url, filename7z + ".temp");

                        File.Move(filename7z + ".temp", filename7z);
                    }

                    string filenameIso = filename + ".iso";
                    if (!File.Exists(filenameIso))
                    {
                        if (File.Exists(filenameIso + ".temp"))
                            File.Delete(filenameIso + ".temp");

                        FileStream fs = File.Create(filenameIso + ".temp");
                        SevenZipExtractor sze = new SevenZipExtractor(filename7z);
                        sze.ExtractFile(filenameIso, fs);
                        fs.Close();

                        File.Move(filenameIso + ".temp", filenameIso);
                    }

                    string filenameIsoUnatt = filename + "_unatt.iso";
                    if (File.Exists(filenameIsoUnatt + ".temp"))
                        File.Delete(filenameIsoUnatt + ".temp");
                    if (File.Exists(filenameIsoUnatt))
                        File.Delete(filenameIsoUnatt);

                    FileStream isofs = File.Open(filenameIso, FileMode.Open, FileAccess.Read);
                    CDReader cdr = new CDReader(isofs, true);
                    CDBuilder cdb = new CDBuilder();
                    cdb.VolumeIdentifier = cdr.VolumeLabel;
                    CloneCdDirectory("", cdr, cdb);
                    cdb.AddFile("reactos\\unattend.inf", "unattend.inf");
                    if (additionalFileName != null)
                        cdb.AddFile(additionalFileName, additionalFileName);

                    Stream bootImgStr = cdr.OpenBootImage();
                    cdb.SetBootImage(bootImgStr, cdr.BootEmulation, cdr.BootLoadSegment);
                    bootImgStr.Close();

                    cdb.Build(filenameIsoUnatt + ".temp");

                    File.Move(filenameIsoUnatt + ".temp", filenameIsoUnatt);


                    string vmName = string.Format("ReactOS_r{0}", revision);
                    string diskName = Environment.CurrentDirectory + "\\" + vmName + "\\" + vmName + ".vdi";
                    string fullIsoName = Environment.CurrentDirectory + "\\" + filenameIsoUnatt;
                    string deleteVmCmd = string.Format("unregistervm --name {0}", vmName);
                    string createVmCmd = string.Format(
                        "createvm --name {0} --basefolder {1} --ostype WindowsXP --register",
                        vmName, Environment.CurrentDirectory);
                    string modifyVmCmd = string.Format("modifyvm {0} --memory 256 --vram 16 --boot1 disk --boot2 dvd", vmName);
                    string storageCtlCmd = string.Format("storagectl {0} --name \"IDE Controller\" --add ide", vmName);
                    string createMediumCmd = string.Format("createmedium disk --filename {0} --size 2048", diskName);
                    string storageAttachCmd1 = string.Format("storageattach {0} --port 0 --device 0 --storagectl \"IDE Controller\" --type hdd --medium {1}", vmName, diskName);
                    string storageAttachCmd2 = string.Format("storageattach {0} --port 1 --device 0 --storagectl \"IDE Controller\" --type dvddrive --medium {1}", vmName, fullIsoName);
                    string startCmd = string.Format("startvm {0}", vmName);

                    Exec(vboxManagePath, deleteVmCmd);
                    Exec(vboxManagePath, createVmCmd);
                    Exec(vboxManagePath, modifyVmCmd);
                    Exec(vboxManagePath, storageCtlCmd);
                    Exec(vboxManagePath, createMediumCmd);
                    Exec(vboxManagePath, storageAttachCmd1);
                    Exec(vboxManagePath, storageAttachCmd2);
                    Exec(vboxManagePath, startCmd);

                    Dispatcher.Invoke(() => { RunButton.IsEnabled = true; });
                });
            task.Start();
        }
    }
}
