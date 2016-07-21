﻿// Copyright (c) 2013, 2016, Oracle and/or its affiliates. All rights reserved.
//
// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License as
// published by the Free Software Foundation; version 2 of the
// License.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA
// 02110-1301  USA

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using MySql.Notifier.Enumerations;
using MySql.Notifier.Properties;
using MySQL.Utility.Classes;
using MySQL.Utility.Classes.MySQLWorkbench;

namespace MySql.Notifier.Classes
{
  /// <summary>
  /// This class serves as a manager of machines and its services for the Notifier class
  /// </summary>
  public class MachinesList : IDisposable
  {
    /// <summary>
    /// Initializes a new instance of the <see cref="MachinesList"/> class.
    /// </summary>
    public MachinesList()
    {
      Machines = Settings.Default.MachineList ?? new List<Machine>();
      ScrubMachinesWithNoServices();
      InitialLoad();
    }

    #region Events

    /// <summary>
    /// Event handler  method for changes on machines list.
    /// </summary>
    /// <param name="machine">Machine that caused the list change.</param>
    /// <param name="listChangeType">List change type.</param>
    public delegate void MachineListChangedHandler(Machine machine, ListChangeType listChangeType);

    /// <summary>
    /// Occurs when a machine is added or removed from the machines list.
    /// </summary>
    public event MachineListChangedHandler MachineListChanged;

    /// <summary>
    /// Occurs when services are added or removed from the list of services of a machine in the machines list.
    /// </summary>
    public event Machine.ServiceListChangedHandler MachineServiceListChanged;

    /// <summary>
    /// Occurs when a service in the services list of a machine in the machines list has a status change.
    /// </summary>
    public event Machine.ServiceStatusChangedHandler MachineServiceStatusChanged;

    /// <summary>
    /// Occurs when an error is thrown while attempting to change the status of a service in the services list of a machine in the machines list.
    /// </summary>
    public event Machine.ServiceStatusChangeErrorHandler MachineServiceStatusChangeError;

    /// <summary>
    /// Occurs when the status of a machine in the machines list changes.
    /// </summary>
    public event Machine.MachineStatusChangedHandler MachineStatusChanged;

    /// <summary>
    /// Occurs when Workbench was installed or uninstall on the local computer.
    /// </summary>
    public event Machine.WorkbenchInstallationChangedHandler WorkbenchInstallationChanged;

    #endregion Events

    /// <summary>
    /// Gets default local machine instance.
    /// </summary>
    public Machine LocalMachine { get; private set; }

    /// <summary>
    /// Gets or sets a list of all machines saved in the settings file.
    /// </summary>
    public List<Machine> Machines { get; private set; }

    /// <summary>
    /// Gets the number of services monitored from all machines in the machines list.
    /// </summary>
    public int ServicesCount
    {
      get
      {
        return Machines != null ? Machines.Sum(m => m.Services.Count) : 0;
      }
    }

    /// <summary>
    /// Adds or removes machines from the machines list.
    /// </summary>
    /// <param name="machine">Machine to add or delete.</param>
    /// <param name="listChangeType">List change type.</param>
    public void ChangeMachine(Machine machine, ListChangeType listChangeType)
    {
      switch (listChangeType)
      {
        case ListChangeType.AutoAdd:
        case ListChangeType.AddByLoad:
        case ListChangeType.AddByUser:
          // If Machine already exists we don't need to do anything else here;
          if (HasMachineWithId(machine.MachineId))
          {
            return;
          }

          Machines.Add(machine);
          break;

        case ListChangeType.RemoveByUser:
          Machines.Remove(machine);
          break;

        case ListChangeType.RemoveByEvent:
          if (machine.Services.Count == 0)
          {
            Machines.Remove(machine);
          }
          break;
      }

      OnMachineListChanged(machine, listChangeType);
    }

    /// <summary>
    /// Releases all resources used by the <see cref="MachinesList"/> class
    /// </summary>
    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases all resources used by the <see cref="MachinesList"/> class
    /// </summary>
    /// <param name="disposing">If true this is called by Dispose(), otherwise it is called by the finalizer</param>
    protected virtual void Dispose(bool disposing)
    {
      if (disposing)
      {
        // Free managed resources
        if (Machines != null)
        {
          foreach (Machine machine in Machines.Where(machine => machine != null))
          {
            machine.Dispose();
          }
        }
      }

      // Add class finalizer if unmanaged resources are added to the class
      // Free unmanaged resources if there are any
    }

    /// <summary>
    /// Gets the machine corresponding to the given machine ID.
    /// </summary>
    /// <param name="id">Id of the machine to find.</param>
    /// <returns>The machine matching the given ID.</returns>
    public Machine GetMachineById(string id)
    {
      return Machines.Count == 0 ? null : Machines.Find(m => m.MachineId == id);
    }

    /// <summary>
    /// Gets the machine corresponding to the given machine name.
    /// </summary>
    /// <param name="machineName">Name of the machine to find.</param>
    /// <returns>The machine matching the given name.</returns>
    public Machine GetMachineByName(string machineName)
    {
      return Machines.Count == 0 ? null : Machines.Find(m => string.Compare(m.Name, machineName, StringComparison.OrdinalIgnoreCase) == 0);
    }

    /// <summary>
    /// Checks if a machine with the given ID exists in the list of machines.
    /// </summary>
    /// <param name="machineId">ID of the machine to search in the list of machines.</param>
    /// <returns>true if a machine with the given ID exists in the list, false otherwise.</returns>
    public bool HasMachineWithId(string machineId)
    {
      return GetMachineById(machineId) != null;
    }

    /// <summary>
    /// Checks if a machine with the given name exists in the list of machines.
    /// </summary>
    /// <param name="machineName">Name of the machine to search in the list of machines.</param>
    /// <returns>true if a machine with the given name exists in the list, false otherwise.</returns>
    public bool HasMachineWithName(string machineName)
    {
      return GetMachineByName(machineName) != null;
    }

    /// <summary>
    /// Performs load operations that had to be done after the settings file was de-serialized.
    /// </summary>
    public void InitialLoad()
    {
      LocalMachine = GetMachineByName(MySqlWorkbenchConnection.DEFAULT_HOSTNAME) ?? new Machine();
      LocalMachine.LoadServicesParameters(true);
      OnMachineListChanged(LocalMachine, ListChangeType.AutoAdd);

      RecreateInvalidScheduledTask();
      MigrateOldServices();

      if (!Settings.Default.FirstRun)
      {
        return;
      }

      CreateScheduledTask();
      AutoAddLocalServices();
      Settings.Default.FirstRun = false;
      SavetoFile();
    }

    /// <summary>
    /// Recreates the scheduled task if it doesn't exist on the system and is supposed to already be there.
    /// </summary>
    private void RecreateInvalidScheduledTask()
    {
      if (!Settings.Default.FirstRun && Settings.Default.AutoCheckForUpdates && !VerifyScheduledTaskExists(Notifier.DefaultTaskName))
      {
        CreateScheduledTask();
      }
    }

    /// <summary>
    /// Creates the scheduled task. If the task already exists it is deleted first.
    /// </summary>
    private void CreateScheduledTask()
    {
      if (Settings.Default.AutoCheckForUpdates && Settings.Default.CheckForUpdatesFrequency > 0 && !String.IsNullOrEmpty(AssemblyInfo.AssemblyTitle))
      {
        Utility.CreateScheduledTask(Notifier.DefaultTaskName, Notifier.DefaultTaskPath, "--c", Settings.Default.CheckForUpdatesFrequency);
      }
    }

    /// <summary>
    /// Refreshes the machines list and subscribes to machine events.
    /// </summary>
    public void LoadMachinesServices()
    {
      var machineIdsList = Machines.ConvertAll(machine => machine.MachineId);
      foreach (Machine machine in machineIdsList.Select(GetMachineById).Where(machine => machine != null))
      {
        OnMachineListChanged(machine, ListChangeType.AddByLoad);
        machine.LoadServicesParameters(false);
      }
    }

    /// <summary>
    /// Saves the list of machines in the settings.config file.
    /// </summary>
    public void SavetoFile()
    {
      Settings.Default.MachineList = Machines;
      Settings.Default.Save();
    }

    /// <summary>
    /// Updates the machines timeout to perform an automatic connection test.
    /// </summary>
    public void UpdateMachinesConnectionTimeouts()
    {
      foreach (var machine in Machines)
      {
        machine.SecondsToAutoTestConnection--;
      }
    }

    /// <summary>
    /// Fires the <see cref="MachineListChanged"/> event.
    /// </summary>
    /// <param name="machine">Machine instance.</param>
    /// <param name="listChangeType">Machine list change type.</param>
    protected virtual void OnMachineListChanged(Machine machine, ListChangeType listChangeType)
    {
      if (listChangeType == ListChangeType.RemoveByEvent || listChangeType == ListChangeType.RemoveByUser)
      {
        int removedServicesQuantity = machine.RemoveAllServices();
        if (removedServicesQuantity > 0)
        {
          SavetoFile();
        }

        if (machine != LocalMachine)
        {
          machine.MachineStatusChanged -= OnMachineStatusChanged;
          machine.ServiceListChanged -= OnMachineServiceListChanged;
          machine.ServiceStatusChanged -= OnMachineServiceStatusChanged;
          machine.ServiceStatusChangeError -= OnMachineServiceStatusChangeError;
        }
      }
      else
      {
        if (listChangeType == ListChangeType.Updated)
        {
          SavetoFile();
        }

        machine.MachineStatusChanged -= OnMachineStatusChanged;
        machine.ServiceListChanged -= OnMachineServiceListChanged;
        machine.ServiceStatusChanged -= OnMachineServiceStatusChanged;
        machine.ServiceStatusChangeError -= OnMachineServiceStatusChangeError;
        machine.WorkbenchInstallationChanged -= OnWorkbenchInstallationChanged;
        machine.MachineStatusChanged += OnMachineStatusChanged;
        machine.ServiceListChanged += OnMachineServiceListChanged;
        machine.ServiceStatusChanged += OnMachineServiceStatusChanged;
        machine.ServiceStatusChangeError += OnMachineServiceStatusChangeError;
        machine.WorkbenchInstallationChanged += OnWorkbenchInstallationChanged;
      }

      if (MachineListChanged != null)
      {
        MachineListChanged(machine, listChangeType);
      }
    }

    /// <summary>
    /// Fires the <see cref="MachineServiceListChanged"/> event.
    /// </summary>
    /// <param name="machine">Machine instance.</param>
    /// <param name="service">MySQLService instance.</param>
    /// <param name="listChangeType">Service list change type.</param>
    protected virtual void OnMachineServiceListChanged(Machine machine, MySqlService service, ListChangeType listChangeType)
    {
      switch (listChangeType)
      {
        case ListChangeType.RemoveByEvent:
        case ListChangeType.RemoveByUser:
          if (MachineServiceListChanged != null)
          {
            MachineServiceListChanged(machine, service, listChangeType);
          }

          if (machine.Services.Count == 0)
          {
            ChangeMachine(machine, ListChangeType.RemoveByEvent);
          }

          break;

        case ListChangeType.AutoAdd:
          if (machine.Services.Count == 1)
          {
            ChangeMachine(machine, ListChangeType.AutoAdd);
          }

          if (MachineServiceListChanged != null)
          {
            MachineServiceListChanged(machine, service, listChangeType);
          }

          break;

        default:
          if (MachineServiceListChanged != null)
          {
            MachineServiceListChanged(machine, service, listChangeType);
          }

          break;
      }

      SavetoFile();
    }

    /// <summary>
    /// Fires the <see cref="MachineServiceStatusChanged"/> event.
    /// </summary>
    /// <param name="machine">Machine instance.</param>
    /// <param name="service">MySQLService instance.</param>
    protected virtual void OnMachineServiceStatusChanged(Machine machine, MySqlService service)
    {
      if (MachineServiceStatusChanged != null)
      {
        MachineServiceStatusChanged(machine, service);
      }
    }

    /// <summary>
    /// Fires the <see cref="MachineServiceStatusChangeError"/> event.
    /// </summary>
    /// <param name="machine">Machine instance.</param>
    /// <param name="service">MySQLService instance.</param>
    /// <param name="ex">Exception thrown while trying to change the service's status.</param>
    protected virtual void OnMachineServiceStatusChangeError(Machine machine, MySqlService service, Exception ex)
    {
      if (MachineServiceStatusChangeError != null)
      {
        MachineServiceStatusChangeError(machine, service, ex);
      }
    }

    /// <summary>
    /// Fires the <see cref="MachineStatusChanged"/> event.
    /// </summary>
    /// <param name="machine">Machine instance.</param>
    /// <param name="oldConnectionStatus">Old connection status.</param>
    protected virtual void OnMachineStatusChanged(Machine machine, Machine.ConnectionStatusType oldConnectionStatus)
    {
      if (MachineStatusChanged != null)
      {
        MachineStatusChanged(machine, oldConnectionStatus);
      }
    }

    /// <summary>
    /// Event delegate method that is fired when Workbench installation changed.
    /// </summary>
    /// <param name="remoteservice">The remoteservice.</param>
    protected virtual void OnWorkbenchInstallationChanged(ManagementBaseObject remoteservice)
    {
      if (WorkbenchInstallationChanged != null)
      {
        WorkbenchInstallationChanged(remoteservice);
      }
    }

    /// <summary>
    /// Adds the local computer and local services found with a specific pattern specified by users.
    /// </summary>
    private void AutoAddLocalServices()
    {
      // Verify if MySQL services are present on the local machine
      string autoAddPattern = Settings.Default.AutoAddPattern;
      ManagementObjectCollection localServicesList = LocalMachine.GetWmiServices(autoAddPattern, true, false);
      List<ManagementObject> servicesToAddList = localServicesList.Cast<ManagementObject>().Where(mo => mo != null && Service.IsRealMySqlService(mo.Properties["Name"].Value.ToString())).ToList();

      // If we found some services we will try to add the local machine to the list...
      if (servicesToAddList.Count <= 0)
      {
        return;
      }

      ChangeMachine(LocalMachine, ListChangeType.AutoAdd);

      // Try to add the services we found on it.
      foreach (MySqlService service in servicesToAddList.Select(mo => new MySqlService(mo.Properties["Name"].Value.ToString(), true, true, LocalMachine)))
      {
        service.SetServiceParameters(true);
        LocalMachine.ChangeService(service, ListChangeType.AutoAdd);
      }
    }

    /// <summary>
    /// Scrubs the machines that for some reasons have no services.
    /// This may be caused by users removing services directly from the settings file.
    /// </summary>
    private void ScrubMachinesWithNoServices()
    {
      // We need to make a copy of the machines list since the original one will be modified.
      var machinesListCopy = Machines.ToList();
      foreach (var machine in machinesListCopy)
      {
        if (machine.Services.Count == 0)
        {
          ChangeMachine(machine, ListChangeType.RemoveByEvent);
        }
      }
    }

    /// <summary>
    /// Merge the old services schema into the new one
    /// </summary>
    private void MigrateOldServices()
    {
      // Load old services schema
      List<MySqlService> services = Settings.Default.ServiceList;

      // Attempt migration only if services were found
      if (services == null || services.Count <= 0)
      {
        return;
      }

      ChangeMachine(LocalMachine, ListChangeType.AutoAdd);

      // Copy services from old schema to the Local machine
      foreach (MySqlService service in services)
      {
        service.Host = LocalMachine;
        service.SetServiceParameters(true);
        LocalMachine.Services.Add(service);
      }

      // Clear the old list of services to erase the duplicates on the newer schema
      services.Clear();
      SavetoFile();
    }

    /// <summary>
    /// Verifies a task with the given name exists in the current machine.
    /// </summary>
    /// <param name="scheduledTaskToFind">The scheduled task name to be found.</param>
    /// <returns>
    ///   <c>true</c> if the schedule task is already created.
    /// </returns>
    public bool VerifyScheduledTaskExists(string scheduledTaskToFind)
    {
      var schtasks = new ProcessStartInfo
      {
        FileName = "schtasks.exe",
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        Arguments = "/query /TN " + scheduledTaskToFind,
        RedirectStandardOutput = true
      };

      using (var process = Process.Start(schtasks))
      {
        if (process == null)
        {
          return false;
        }

        using (var reader = process.StandardOutput)
        {
          var stdout = reader.ReadToEnd();
          return stdout.Contains(scheduledTaskToFind);
        }
      }
    }
  }
}