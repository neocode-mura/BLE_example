using Microsoft.VisualBasic.CompilerServices;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// 空白ページの項目テンプレートについては、https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x411 を参照してください

namespace BLE_example
{
    public sealed partial class MainPage : Page
    {
        private ObservableCollection<BluetoothLEDeviceDisplay> KnownDevices = new ObservableCollection<BluetoothLEDeviceDisplay>();
        private List<DeviceInformation> UnknownDevices = new List<DeviceInformation>();
        private DeviceWatcher deviceWatcher;
        private BluetoothLEDevice bluetoothLeDevice = null;
        private BluetoothLEDeviceDisplay bleDeviceDisplay = null;
        private GattPresentationFormat presentationFormat;
        IReadOnlyList<GattCharacteristic> characteristics;

        #region Error Codes
        readonly int E_BLUETOOTH_ATT_WRITE_NOT_PERMITTED = unchecked((int)0x80650003);
        readonly int E_BLUETOOTH_ATT_INVALID_PDU = unchecked((int)0x80650004);
        readonly int E_ACCESSDENIED = unchecked((int)0x80070005);
        readonly int E_DEVICE_NOT_AVAILABLE = unchecked((int)0x800710df);
        #endregion

        #region UI Code
        public MainPage()
        {
            this.InitializeComponent();
        }
        private void Button_Click(object sender, RoutedEventArgs e)
        {
            Splitter.IsPaneOpen = !Splitter.IsPaneOpen;
        }
        private void EnumerateingButton_Click(object sender, RoutedEventArgs e)
        {
            if (deviceWatcher == null)
            {
                StartBleDeviceWatcher();
                EnumerateButton.Content = "Stop enumerating";
                NotifyUser($"Device watcher started.", NotifyType.StatusMessage);
            }
            else
            {
                StopBleDeviceWatcher();
                EnumerateButton.Content = "Start enumerating";
                NotifyUser($"Device watcher stopped.", NotifyType.StatusMessage);
            }
        }
        private void ItemListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bleDeviceDisplay = ResultsListView.SelectedItem as BluetoothLEDeviceDisplay;
            if(bleDeviceDisplay != null)
            {
                PairButton.IsEnabled = !bleDeviceDisplay.DeviceInformation.Pairing.IsPaired;
                ConnectButton.IsEnabled = ((bool?)bleDeviceDisplay.DeviceInformation.Properties["System.Devices.Aep.IsConnected"] == false) 
                    && bleDeviceDisplay.DeviceInformation.Pairing.IsPaired;
            }
        }
        private async void CharacteristicReadButton_Click(object sender, RoutedEventArgs e)
        {
            // BT_Code: Read the actual value from the device by using Uncached.
            GattReadResult result = await characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
            if (result.Status == GattCommunicationStatus.Success)
            {
                string formattedResult = FormatValueByPresentation(result.Value, presentationFormat);
                NotifyUser($"Read result: {formattedResult}", NotifyType.StatusMessage);
                CharacteristicReadValue.Text = formattedResult;
            }
            else
            {
                NotifyUser($"Read failed: {result.Status}", NotifyType.ErrorMessage);
            }
        }
        private async void CharacteristicWriteButtonInt_Click(object sender, RoutedEventArgs e)
        {
            if (!String.IsNullOrEmpty(CharacteristicWriteValue.Text))
            {
                var isValidValue = Int32.TryParse(CharacteristicWriteValue.Text, out int readValue);
                if (isValidValue)
                {
                    var writer = new DataWriter();
                    writer.ByteOrder = ByteOrder.LittleEndian;
                    writer.WriteInt32(readValue);

                    var writeSuccessful = await WriteBufferToCustomCharacteristicAsync(writer.DetachBuffer());
                }
                else
                {
                    NotifyUser("Data to write has to be an int32", NotifyType.ErrorMessage);
                }
            }
            else
            {
                NotifyUser("No data to write to device", NotifyType.ErrorMessage);
            }
        }
        private async void CharacteristicWriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (!String.IsNullOrEmpty(CharacteristicWriteValue.Text))
            {
                var writeBuffer = CryptographicBuffer.ConvertStringToBinary(CharacteristicWriteValue.Text,
                    BinaryStringEncoding.Utf8);

                var writeSuccessful = await WriteBufferToCustomCharacteristicAsync(writeBuffer);
            }
            else
            {
                NotifyUser("No data to write to device", NotifyType.ErrorMessage);
            }
        }
        #endregion

        #region Device Discovery
        private void StartBleDeviceWatcher()
        {
            // Additional properties we would like about the device.
            // Property strings are documented here https://msdn.microsoft.com/en-us/library/windows/desktop/ff521659(v=vs.85).aspx
            string[] requestedProperties = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected", "System.Devices.Aep.Bluetooth.Le.IsConnectable" };

            // BT_Code: Example showing paired and non-paired in a single query.
            string aqsAllBluetoothLEDevices = "(System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")";

            deviceWatcher = DeviceInformation.CreateWatcher(
                aqsAllBluetoothLEDevices,
                requestedProperties,
                DeviceInformationKind.AssociationEndpoint);

            // Register event handlers before starting the watcher.
            deviceWatcher.Added += DeviceWatcher_Added;
            deviceWatcher.Updated += DeviceWatcher_Updated;
            deviceWatcher.Removed += DeviceWatcher_Removed;
            deviceWatcher.EnumerationCompleted += DeviceWatcher_EnumerationCompleted;
            deviceWatcher.Stopped += DeviceWatcher_Stopped;

            KnownDevices.Clear();

            deviceWatcher.Start();
        }
        private void StopBleDeviceWatcher()
        {
            if (deviceWatcher != null)
            {
                // Unregister the event handlers.
                deviceWatcher.Added -= DeviceWatcher_Added;
                deviceWatcher.Updated -= DeviceWatcher_Updated;
                deviceWatcher.Removed -= DeviceWatcher_Removed;
                deviceWatcher.EnumerationCompleted -= DeviceWatcher_EnumerationCompleted;
                deviceWatcher.Stopped -= DeviceWatcher_Stopped;

                // Stop the watcher.
                deviceWatcher.Stop();
                deviceWatcher = null;
            }
        }
        private BluetoothLEDeviceDisplay FindBluetoothLEDeviceDisplay(string id)
        {
            foreach (BluetoothLEDeviceDisplay bleDeviceDisplay in KnownDevices)
            {
                if (bleDeviceDisplay.Id == id)
                {
                    return bleDeviceDisplay;
                }
            }
            return null;
        }
        private DeviceInformation FindUnknownDevices(string id)
        {
            foreach (DeviceInformation bleDeviceInfo in UnknownDevices)
            {
                if (bleDeviceInfo.Id == id)
                {
                    return bleDeviceInfo;
                }
            }
            return null;
        }
        private async void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation deviceInfo)
        {
            // We must update the collection on the UI thread because the collection is databound to a UI element.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                lock (this)
                {
                    Debug.WriteLine(String.Format("Added {0}{1}", deviceInfo.Id, deviceInfo.Name));

                    // Protect against race condition if the task runs after the app stopped the deviceWatcher.
                    if (sender == deviceWatcher)
                    {
                        // Make sure device isn't already present in the list.
                        if (FindBluetoothLEDeviceDisplay(deviceInfo.Id) == null)
                        {
                            if (deviceInfo.Name != string.Empty)
                            {
                                // If device has a friendly name display it immediately.
                                KnownDevices.Add(new BluetoothLEDeviceDisplay(deviceInfo));
                            }
                            else
                            {
                                // Add it to a list in case the name gets updated later. 
                                UnknownDevices.Add(deviceInfo);
                            }
                        }
                    }
                }
            });
        }
        private async void DeviceWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate deviceInfoUpdate)
        {
            // We must update the collection on the UI thread because the collection is databound to a UI element.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                lock (this)
                {
                    Debug.WriteLine(String.Format("Updated {0}{1}", deviceInfoUpdate.Id, ""));

                    // Protect against race condition if the task runs after the app stopped the deviceWatcher.
                    if (sender == deviceWatcher)
                    {
                        BluetoothLEDeviceDisplay bleDeviceDisplay = FindBluetoothLEDeviceDisplay(deviceInfoUpdate.Id);
                        if (bleDeviceDisplay != null)
                        {
                            // Device is already being displayed - update UX.
                            bleDeviceDisplay.Update(deviceInfoUpdate);
                            return;
                        }

                        DeviceInformation deviceInfo = FindUnknownDevices(deviceInfoUpdate.Id);
                        if (deviceInfo != null)
                        {
                            deviceInfo.Update(deviceInfoUpdate);
                            // If device has been updated with a friendly name it's no longer unknown.
                            if (deviceInfo.Name != String.Empty)
                            {
                                KnownDevices.Add(new BluetoothLEDeviceDisplay(deviceInfo));
                                UnknownDevices.Remove(deviceInfo);
                            }
                        }
                    }
                }
            });
        }
        private async void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate deviceInfoUpdate)
        {
            // We must update the collection on the UI thread because the collection is databound to a UI element.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                lock (this)
                {
                    Debug.WriteLine(String.Format("Removed {0}{1}", deviceInfoUpdate.Id, ""));

                    // Protect against race condition if the task runs after the app stopped the deviceWatcher.
                    if (sender == deviceWatcher)
                    {
                        // Find the corresponding DeviceInformation in the collection and remove it.
                        BluetoothLEDeviceDisplay bleDeviceDisplay = FindBluetoothLEDeviceDisplay(deviceInfoUpdate.Id);
                        if (bleDeviceDisplay != null)
                        {
                            KnownDevices.Remove(bleDeviceDisplay);
                        }

                        DeviceInformation deviceInfo = FindUnknownDevices(deviceInfoUpdate.Id);
                        if (deviceInfo != null)
                        {
                            UnknownDevices.Remove(deviceInfo);
                        }
                    }
                }
            });
        }
        private async void DeviceWatcher_EnumerationCompleted(DeviceWatcher sender, object e)
        {
            // We must update the collection on the UI thread because the collection is databound to a UI element.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                // Protect against race condition if the task runs after the app stopped the deviceWatcher.
                if (sender == deviceWatcher)
                {
                    NotifyUser($"{KnownDevices.Count} devices found. Enumeration completed.",
                        NotifyType.StatusMessage);
                }
            });
        }
        private async void DeviceWatcher_Stopped(DeviceWatcher sender, object e)
        {
            // We must update the collection on the UI thread because the collection is databound to a UI element.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                // Protect against race condition if the task runs after the app stopped the deviceWatcher.
                if (sender == deviceWatcher)
                {
                    NotifyUser($"No longer watching for devices.",
                            sender.Status == DeviceWatcherStatus.Aborted ? NotifyType.ErrorMessage : NotifyType.StatusMessage);
                }
            });
        }
        #endregion

        #region Pairing
        private bool isBusy = false;

        private async void PairButton_Click(object sender, RoutedEventArgs e)
        {
            // Do not allow a new Pair operation to start if an existing one is in progress.
            if (isBusy)
            {
                return;
            }

            isBusy = true;

            NotifyUser("Pairing started. Please wait...", NotifyType.StatusMessage);
            NotifyType type;

            // For more information about device pairing, including examples of
            // customizing the pairing process, see the DeviceEnumerationAndPairing sample.

            // Capture the current selected item in case the user changes it while we are pairing.
            var bleDeviceDisplay = ResultsListView.SelectedItem as BluetoothLEDeviceDisplay;

            // BT_Code: Pair the currently selected device.
            DevicePairingResult result = await bleDeviceDisplay.DeviceInformation.Pairing.PairAsync();
            if(result.Status == DevicePairingResultStatus.Paired || result.Status == DevicePairingResultStatus.AlreadyPaired)
            {
                type = NotifyType.StatusMessage;
                PairButton.IsEnabled = false;
                ConnectButton.IsEnabled = true;
            }
            else
            {
                type = NotifyType.ErrorMessage;
            }
            NotifyUser($"Pairing result = {result.Status}", type);

            isBusy = false;
        }

        #endregion

        #region Enumerationg Service
        private void ClearBluetoothLEDeviceAsync()
        {
            bluetoothLeDevice?.Dispose();
            bluetoothLeDevice = null;
        }
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            ConnectButton.IsEnabled = false;

            ClearBluetoothLEDeviceAsync();

            try
            {
                // BT_Code: BluetoothLEDevice.FromIdAsync must be called from a UI thread because it may prompt for consent.
                bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(bleDeviceDisplay.Id);

                if (bluetoothLeDevice == null)
                {
                    NotifyUser("Failed to connect to device.", NotifyType.ErrorMessage);
                    ConnectButton.IsEnabled = true;                
                }
            }
            catch (Exception ex) when (ex.HResult == E_DEVICE_NOT_AVAILABLE)
            {
                NotifyUser("Bluetooth radio is not on.", NotifyType.ErrorMessage);
                ConnectButton.IsEnabled = true;                
            }

            if (bluetoothLeDevice != null)
            {
                // Note: BluetoothLEDevice.GattServices property will return an empty list for unpaired devices. For all uses we recommend using the GetGattServicesAsync method.
                // BT_Code: GetGattServicesAsync returns a list of all the supported services of the device (even if it's not paired to the system).
                // If the services supported by the device are expected to change during BT usage, subscribe to the GattServicesChanged event.
                GattDeviceServicesResult result = await bluetoothLeDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);

                if (result.Status == GattCommunicationStatus.Success)
                {
                    var services = result.Services;
                    string serviceName;

                    foreach (var service in services)
                    {
                        serviceName = DisplayHelpers.GetServiceName(service);
                        if(serviceName.IndexOf("Custom Service") >= 0)
                        {
                            NotifyUser("Found custom service", NotifyType.StatusMessage);
                            ServiceUUID.Text = DisplayHelpers.GetServiceName(service);
                            ServiceUUIDBorder.Visibility = Visibility.Visible;
                            EnumeratingChara(service);
                            ConnectButton.IsEnabled = false;
                            break;
                        }
                        else
                        {
                            NotifyUser("Not found customr service", NotifyType.ErrorMessage);
                            ConnectButton.IsEnabled = true;                
                        }
                    }
                }
                else
                {
                    NotifyUser("Device unreachable", NotifyType.ErrorMessage);
                    ConnectButton.IsEnabled = true;                
                }
            }

        }
        #endregion

        #region Enumerating Characteristics
        private async void EnumeratingChara(GattDeviceService service)
        {
            string charaName;
            characteristics = null;
            try
            {
                var accessStatus = await service.RequestAccessAsync();
                if(accessStatus == DeviceAccessStatus.Allowed)
                {
                    var result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    if (result.Status == GattCommunicationStatus.Success)
                    {
                        characteristics = result.Characteristics;
                    }
                    else
                    {
                        NotifyUser("Error accessing service.", NotifyType.ErrorMessage);
                        characteristics = new List<GattCharacteristic>();
                    }
                }
                else
                {
                    NotifyUser("Error accessing service.", NotifyType.ErrorMessage);
                    characteristics = new List<GattCharacteristic>();
                }
            }
            catch(Exception ex)
            {
                NotifyUser("Restricted service. Can't read charcteristics:" + ex.Message,NotifyType.ErrorMessage);
                characteristics = new List<GattCharacteristic>();
            }
            var chara = characteristics[0];
            charaName = DisplayHelpers.GetCharacteristicName(chara);
            CharaReadUUID.Text = charaName;
            CharaReadUUIDBorder.Visibility = Visibility.Visible;
            EnumerationReadDescriptor(chara);

            chara = characteristics[1];
            charaName = DisplayHelpers.GetCharacteristicName(chara);
            CharaWriteUUID.Text = charaName;
            CharaWriteUUIDBorder.Visibility = Visibility.Visible;
            EnumerationWriteDescriptor(chara);
            CharaReadUUIDBorder.Visibility = Visibility.Visible;
        }
        #endregion

        private async void EnumerationReadDescriptor(GattCharacteristic chara)
        {
            var result = await chara.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success)
            {
                NotifyUser("Descripter read failure:" + result.Status.ToString(), NotifyType.ErrorMessage);
            }
            presentationFormat = null;
            if (chara.PresentationFormats.Count > 0)
            {
                if(chara.PresentationFormats.Count.Equals(1))
                {
                    presentationFormat = chara.PresentationFormats[0];
                }
            }
            SetVisibility(CharacteristicReadPanel, chara.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read));
        }
        private async void EnumerationWriteDescriptor(GattCharacteristic chara)
        {
            var result = await chara.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success)
            {
                NotifyUser("Descripter read failure:" + result.Status.ToString(), NotifyType.ErrorMessage);
            }
            SetVisibility(CharacteristicWritePanel,
                chara.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ||
                chara.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse));
            CharacteristicWriteValue.Text = "";
        }
        private async Task<bool> WriteBufferToCustomCharacteristicAsync(IBuffer buffer)
        {
            try
            {
                // BT_Code: Writes the value from the buffer to the characteristic.
                var result = await characteristics[1].WriteValueWithResultAsync(buffer);

                if (result.Status == GattCommunicationStatus.Success)
                {
                    NotifyUser("Successfully wrote value to device", NotifyType.StatusMessage);
                    return true;
                }
                else
                {
                    NotifyUser($"Write failed: {result.Status}", NotifyType.ErrorMessage);
                    return false;
                }
            }
            catch (Exception ex) when (ex.HResult == E_BLUETOOTH_ATT_INVALID_PDU)
            {
                NotifyUser(ex.Message, NotifyType.ErrorMessage);
                return false;
            }
            catch (Exception ex) when (ex.HResult == E_BLUETOOTH_ATT_WRITE_NOT_PERMITTED || ex.HResult == E_ACCESSDENIED)
            {
                // This usually happens when a device reports that it support writing, but it actually doesn't.
                NotifyUser(ex.Message, NotifyType.ErrorMessage);
                return false;
            }
        }
        private void SetVisibility(UIElement element, bool visible)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        private string FormatValueByPresentation(IBuffer buffer, GattPresentationFormat format)
        {
            // BT_Code: For the purpose of this sample, this function converts only UInt32 and
            // UTF-8 buffers to readable text. It can be extended to support other formats if your app needs them.
            byte[] data;
            CryptographicBuffer.CopyToByteArray(buffer, out data);
            if (format != null)
            {
                if (format.FormatType == GattPresentationFormatTypes.UInt32 && data.Length >= 4)
                {
                    return BitConverter.ToInt32(data, 0).ToString();
                }
                else if (format.FormatType == GattPresentationFormatTypes.Utf8)
                {
                    try
                    {
                        return Encoding.UTF8.GetString(data);
                    }
                    catch (ArgumentException)
                    {
                        return "(error: Invalid UTF-8 string)";
                    }
                }
                else
                {
                    // Add support for other format types as needed.
                    return "Unsupported format: " + CryptographicBuffer.EncodeToHexString(buffer);
                }
            }
            else if (data != null)
            {
                // We don't know what format to use. Let's try some well-known profiles, or default back to UTF-8.
                return "Unknown format: " + Encoding.UTF8.GetString(data);
            }
            else
            {
                return "Empty data received";
            }
        }
        public void NotifyUser(string strMessage, NotifyType type)
        {
            // If called from the UI thread, then update immediately.
            // Otherwise, schedule a task on the UI thread to perform the update.
            if (Dispatcher.HasThreadAccess)
            {
                UpdateStatus(strMessage, type);
            }
            else
            {
                var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateStatus(strMessage, type));
            }
        }
        private void UpdateStatus(string strMessage, NotifyType type)
        {
            switch (type)
            {
                case NotifyType.StatusMessage:
                    StatusBorder.Background = new SolidColorBrush(Windows.UI.Colors.Green);
                    break;
                case NotifyType.ErrorMessage:
                    StatusBorder.Background = new SolidColorBrush(Windows.UI.Colors.Red);
                    break;
            }

            StatusBlock.Text = strMessage;

            // Collapse the StatusBlock if it has no text to conserve real estate.
            StatusBorder.Visibility = (StatusBlock.Text != String.Empty) ? Visibility.Visible : Visibility.Collapsed;
            if (StatusBlock.Text != String.Empty)
            {
                StatusBorder.Visibility = Visibility.Visible;
                StatusPanel.Visibility = Visibility.Visible;
            }
            else
            {
                StatusBorder.Visibility = Visibility.Collapsed;
                StatusPanel.Visibility = Visibility.Collapsed;
            }

			// Raise an event if necessary to enable a screen reader to announce the status update.
			var peer = FrameworkElementAutomationPeer.FromElement(StatusBlock);
			if (peer != null)
			{
				peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
			}
		}
        public enum NotifyType
        {
            StatusMessage,
            ErrorMessage
        };

    }
}
