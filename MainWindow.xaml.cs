using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;

using Microsoft.Win32;
using DiscUtils.Iso9660;
using SevenZip;

namespace RosRegTest
{
    public partial class MainWindow : Window
    {
        private string vboxManagePath;
        private Dictionary<int, string> revToUrl;

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

        private List<int> GetRevList(WebClient wc, string url)
        {
            List<int> revList = new List<int>();

            byte[] rawHtmlData = wc.DownloadData(url);
            string rawStr = Encoding.UTF8.GetString(rawHtmlData);

            MatchCollection matches = Regex.Matches(
                rawStr, "<a href=\"bootcd-([0-9]+)-dbg\\.7z");
            foreach (Match match in matches)
                revList.Add(int.Parse(match.Groups[1].Value));

            return revList;
        }

        private List<int> GetAdditionalRevList(WebClient wc, int startRev, int endRev)
        {
            SortedSet<int> revSet = new SortedSet<int>();

            int pageStartRev = startRev;
            bool moreFiles = false;
            for (; ; )
            {
                byte[] rawHtmlData = wc.DownloadData(string.Format(
                    "https://iso.reactos.org/scripts/ajax-getfiles-provider.php?" +
                    "filelist=1&startrev={0}&endrev={1}&bootcd-dbg=1&" +
                    "livecd-dbg=0&bootcd-rel=0&livecd-rel=0",
                    pageStartRev, endRev));
                string rawStr = Encoding.UTF8.GetString(rawHtmlData);

                MatchCollection matches = Regex.Matches(
                    rawStr, "<name>bootcd-([0-9]+)-dbg\\.7z</name>");
                foreach (Match match in matches)
                    revSet.Add(int.Parse(match.Groups[1].Value));

                moreFiles = rawStr.IndexOf("<morefiles>1</morefiles>") != -1;
                if (!moreFiles)
                    break;

                pageStartRev = int.Parse(
                    Regex.Match(rawStr, "<lastrev>([0-9]+)</lastrev>").Groups[1].Value);
            }

            return revSet.ToList();
        }

        void WriteRevList(List<int> revList, string fileName)
        {
            string listStr = string.Join("\n", revList.ToArray());
            File.WriteAllText(fileName, listStr, Encoding.ASCII);
        }

        int[] ReadRevList(string fileName)
        {
            string list = File.ReadAllText(fileName, Encoding.ASCII);
            string[] spl = list.Split('\n');
            return Array.ConvertAll(spl, int.Parse);
        }

        private void InitRevList()
        {
            WebClient wc = new WebClient();

            revToUrl = new Dictionary<int, string>();

            List<int> bootcdOldRevs = new List<int>();
            if (!File.Exists("bootcd_old_rev_list.txt"))
            {
                bootcdOldRevs = GetRevList(wc, "https://iso.reactos.org/bootcd_old/");
                WriteRevList(bootcdOldRevs, "bootcd_old_rev_list.txt");
            }
            else
            {
                bootcdOldRevs.AddRange(ReadRevList("bootcd_old_rev_list.txt"));
            }

            List<int> bootcdRevs = new List<int>();
            if (!File.Exists("bootcd_rev_list.txt"))
            {
                bootcdRevs = GetRevList(wc, "https://iso.reactos.org/bootcd/");
                WriteRevList(bootcdRevs, "bootcd_rev_list.txt");
            }
            else
            {
                bootcdRevs.AddRange(ReadRevList("bootcd_rev_list.txt"));

                string url = "https://iso.reactos.org/bootcd/latest_rev";
                int lastRemoteRev = int.Parse(Encoding.ASCII.GetString(wc.DownloadData(url)));
                int lastLocalRev = bootcdRevs[bootcdRevs.Count - 1];

                if (lastLocalRev != lastRemoteRev)
                {
                    if (lastRemoteRev - lastLocalRev > 200)
                        bootcdRevs = GetRevList(wc, "https://iso.reactos.org/bootcd/");
                    else
                        bootcdRevs.AddRange(GetAdditionalRevList(wc, lastLocalRev + 1, lastRemoteRev));
                    WriteRevList(bootcdRevs, "bootcd_rev_list.txt");
                }
            }

            foreach (int revision in bootcdOldRevs)
                revToUrl[revision] = string.Format("{0}bootcd-{1}-dbg.7z", "https://iso.reactos.org/bootcd_old/", revision);
            foreach (int revision in bootcdRevs)
                revToUrl[revision] = string.Format("{0}bootcd-{1}-dbg.7z", "https://iso.reactos.org/bootcd/", revision);
        }

        public MainWindow()
        {
            InitializeComponent();

            if (!InitVBoxManagePath())
            {
                MessageBox.Show("VirtualBox installation not found",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Thread thread = new Thread(() =>
            {
                InitRevList();
                Dispatcher.Invoke(() =>
                {
                    RevTextBox.Text = revToUrl.Keys.Max().ToString();
                    RevTextBox.CaretIndex = RevTextBox.Text.Length;
                    AutoStartCheckBox.IsEnabled = false;
                    SyncLabel.Visibility = Visibility.Hidden;
                    RevTextBox.Focus();
                });
            });
            thread.Start();
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

        private string Run(int revision, string additionalFilePath, bool autoStartChecked)
        {
            string filename = string.Format("bootcd-{0}-dbg", revision);

            string filename7z = filename + ".7z";
            string filenameIso = filename + ".iso";

            if (!File.Exists(filenameIso))
            {
                string filename7zTemp = filename7z + ".temp";
                string filenameIsoTemp = filenameIso + ".temp";

                if (!File.Exists(filename7z))
                {
                    File.Delete(filename7zTemp);

                    WebClient wc = new WebClient();
                    try
                    {
                        wc.DownloadFile(revToUrl[revision], filename7zTemp);
                    }
                    catch (WebException)
                    {
                        return "File download failed:\n  '" + revToUrl[revision] + "'";
                    }

                    File.Move(filename7zTemp, filename7z);
                }

                File.Delete(filenameIsoTemp);

                FileStream isotmpfs = File.Create(filenameIsoTemp);
                FileStream szfs = File.Open(filename7z, FileMode.Open, FileAccess.Read);
                SevenZipExtractor sze = new SevenZipExtractor(szfs);
                sze.ExtractFile(filenameIso, isotmpfs);
                isotmpfs.Close();
                szfs.Close();

                File.Move(filenameIsoTemp, filenameIso);
                File.Delete(filename7z);
            }


            string vmName = string.Format("ReactOS_r{0}", revision);
            string diskName = Environment.CurrentDirectory + "\\" + vmName + "\\" + vmName + ".vdi";

            if (File.Exists(diskName))
            {
                FileStream fs = null;
                try
                {
                    fs = File.Open(diskName, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                catch (IOException)
                {
                    return "Virtual machine '" + vmName + "' is already running";
                }
                finally
                {
                    if (fs != null)
                        fs.Close();
                }
            }


            string filenameIsoUnatt = filename + "_unatt.iso";
            string filenameIsoUnattTemp = filenameIsoUnatt + ".temp";
            File.Delete(filenameIsoUnattTemp);
            File.Delete(filenameIsoUnatt);

            FileStream isofs = File.Open(filenameIso, FileMode.Open, FileAccess.Read);
            CDReader cdr = new CDReader(isofs, true);
            CDBuilder cdb = new CDBuilder();
            cdb.VolumeIdentifier = cdr.VolumeLabel;
            CloneCdDirectory("", cdr, cdb);

            if (!File.Exists("unattend.inf"))
                File.WriteAllText("unattend.inf", RosRegTest.Resources.unattend, Encoding.ASCII);

            string additionalFileName = null;
            if (additionalFilePath != null)
                additionalFileName = Path.GetFileName(additionalFilePath);

            string unattText = File.ReadAllText("unattend.inf", Encoding.ASCII);
            if (autoStartChecked && (additionalFileName != null))
                unattText = unattText + "[GuiRunOnce]\n" + "cmd.exe /c start d:\\" + additionalFileName + "\n\n";

            cdb.AddFile("reactos\\unattend.inf", Encoding.ASCII.GetBytes(unattText));
            if (additionalFileName != null)
                cdb.AddFile(additionalFileName, additionalFilePath);

            Stream bootImgStr = cdr.OpenBootImage();
            cdb.SetBootImage(bootImgStr, cdr.BootEmulation, cdr.BootLoadSegment);
            bootImgStr.Close();

            cdb.Build(filenameIsoUnattTemp);
            isofs.Close();

            File.Move(filenameIsoUnattTemp, filenameIsoUnatt);


            string fullIsoName = Environment.CurrentDirectory + "\\" + filenameIsoUnatt;
            string deleteVmCmd = string.Format("unregistervm --name {0}", vmName);
            string createVmCmd = string.Format(
                "createvm --name {0} --basefolder {1} --ostype WindowsXP --register",
                vmName, Environment.CurrentDirectory);
            string modifyVmCmd = string.Format("modifyvm {0} --memory 256 --vram 16 --nictype1 Am79C973 --audio none --boot1 disk --boot2 dvd", vmName);
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

            return null;
        }

        private bool CheckFile(string fileName)
        {
            if (!File.Exists(fileName))
            {
                MessageBox.Show("File not found: '" + fileName + "'", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return false;
            }
            return true;
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            int revision = 0;
            if (!int.TryParse(RevTextBox.Text, out revision))
                return;
            if (!revToUrl.ContainsKey(revision))
                return;

            string additionalFilePath = null;
            if (AddFileTextBox.Text != "")
                additionalFilePath = AddFileTextBox.Text.TrimEnd();

            if (additionalFilePath != null)
                if (!CheckFile(additionalFilePath))
                    return;

            bool autoStartChecked = AutoStartCheckBox.IsChecked == true;

            RunButton.IsEnabled = false;
            RevTextBox.IsEnabled = false;
            AddFileTextBox.IsEnabled = false;
            AutoStartCheckBox.IsEnabled = false;

            Thread thread = new Thread(() =>
                {
                    string errorMsg = Run(revision, additionalFilePath, autoStartChecked);
                    if (errorMsg != null)
                    {
                        MessageBox.Show(errorMsg, "Operation cancelled",
                            MessageBoxButton.OK, MessageBoxImage.Exclamation);
                    }

                    Dispatcher.Invoke(() =>
                    {
                        RunButton.IsEnabled = true;
                        RevTextBox.IsEnabled = true;
                        AddFileTextBox.IsEnabled = true;
                        AutoStartCheckBox.IsEnabled = AddFileTextBox.Text != "";
                    });
                });
            thread.Start();
        }

        private void RevTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            int revision = 0;
            int.TryParse(RevTextBox.Text, out revision);
            RunButton.IsEnabled = revToUrl.ContainsKey(revision);
        }

        private void AddFileTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            AutoStartCheckBox.IsEnabled = AddFileTextBox.Text != "";
        }
    }
}
