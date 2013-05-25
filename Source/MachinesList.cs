﻿//
// Copyright (c) 2013, Oracle and/or its affiliates. All rights reserved.
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
//

namespace MySql.Notifier
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Management;
  using MySql.Notifier.Properties;
  using MySQL.Utility;

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
      InitialLoad();
    }

    #region Events

    /// <summary>
    /// Event handler  method for changes on machines list.
    /// </summary>
    /// <param name="machine">Machine that caused the list change.</param>
    /// <param name="changeType">List change type.</param>
    public delegate void MachineListChangedHandler(Machine machine, ChangeType changeType);

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
    /// <param name="changeType">List change type.</param>
    public void ChangeMachine(Machine machine, ChangeType changeType)
    {
      switch (changeType)
      {
        case ChangeType.AutoAdd:
        case ChangeType.AddByLoad:
        case ChangeType.AddByUser:
          //// If Machine already exists we don't need to do anything else here;
          if (HasMachineWithId(machine.MachineId))
          {
            return;
          }

          Machines.Add(machine);
          break;

        case ChangeType.RemoveByUser:
        case ChangeType.RemoveByEvent:
          if (machine.Services.Count == 0)
          {
            Machines.Remove(machine);
          }
          break;
      }

      OnMachineListChanged(machine, changeType);
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
        //// Free managed resources
        if (Machines != null)
        {
          foreach (Machine machine in Machines)
          {
            if (machine != null)
            {
              machine.Dispose();
            }
          }
        }
      }

      //// Add class finalizer if unmanaged resources are added to the class
      //// Free unmanaged resources if there are any
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
      return Machines.Count == 0 ? null : Machines.Find(m => string.Compare(m.Name, machineName, true) == 0);
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

      MigrateOldServices();
      AutoAddLocalServices();
    }

    /// <summary>
    /// Refreshes the machines list and subscribes to machine events.
    /// </summary>
    public void LoadMachinesServices()
    {
      var machineIdsList = Machines.ConvertAll<string>(machine => machine.MachineId);
      foreach (string machineId in machineIdsList)
      {
        Machine machine = GetMachineById(machineId);
        if (machine != null)
        {
          OnMachineListChanged(machine, ChangeType.AddByLoad);
          machine.LoadServicesParameters(false);
        }
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
    /// <param name="machine"></param>
    /// <param name="changeType"></param>
    protected virtual void OnMachineListChanged(Machine machine, ChangeType changeType)
    {
      machine.MachineStatusChanged -= OnMachineStatusChanged;
      machine.ServiceListChanged -= OnMachineServiceListChanged;
      machine.ServiceStatusChanged -= OnMachineServiceStatusChanged;
      machine.ServiceStatusChangeError -= OnMachineServiceStatusChangeError;

      if (changeType == ChangeType.RemoveByEvent || changeType == ChangeType.RemoveByUser)
      {
        int removedServicesQuantity = machine.RemoveAllServices();
        if (removedServicesQuantity > 0)
        {
          SavetoFile();
        }
      }
      else
      {
        machine.MachineStatusChanged += OnMachineStatusChanged;
        machine.ServiceListChanged += OnMachineServiceListChanged;
        machine.ServiceStatusChanged += OnMachineServiceStatusChanged;
        machine.ServiceStatusChangeError += OnMachineServiceStatusChangeError;
      }

      if (MachineListChanged != null)
      {
        MachineListChanged(machine, changeType);
      }
    }

    /// <summary>
    /// Fires the <see cref="MachineServiceListChanged"/> event.
    /// </summary>
    /// <param name="machine">Machine instance.</param>
    /// <param name="service">MySQLService instance.</param>
    /// <param name="changeType">Service list change type.</param>
    protected virtual void OnMachineServiceListChanged(Machine machine, MySQLService service, ChangeType changeType)
    {
      if (MachineServiceListChanged != null)
      {
        MachineServiceListChanged(machine, service, changeType);
      }

      switch (changeType)
      {
        case ChangeType.RemoveByEvent:
        case ChangeType.RemoveByUser:
          if (machine.Services.Count == 0)
          {
            ChangeMachine(machine, ChangeType.RemoveByEvent);
          }
          SavetoFile();
          break;

        case ChangeType.AddByUser:
          SavetoFile();
          break;

        case ChangeType.AutoAdd:
          if (machine.Services.Count == 1)
          {
            ChangeMachine(machine, ChangeType.AutoAdd);
          }
          SavetoFile();
          break;
      }
    }

    /// <summary>
    /// Fires the <see cref="MachineServiceStatusChanged"/> event.
    /// </summary>
    /// <param name="machine">Machine instance.</param>
    /// <param name="service">MySQLService instance.</param>
    protected virtual void OnMachineServiceStatusChanged(Machine machine, MySQLService service)
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
    protected virtual void OnMachineServiceStatusChangeError(Machine machine, MySQLService service, Exception ex)
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
    /// Adds the local computer and local services found with a specific pattern specified by users.
    /// </summary>
    private void AutoAddLocalServices()
    {
      if (!Settings.Default.FirstRun)
      {
        return;
      }

      //// Verify if MySQL services are present on the local machine
      string autoAddPattern = Settings.Default.AutoAddPattern;
      ManagementObjectCollection localServicesList = LocalMachine.GetWMIServices(autoAddPattern, true, false);
      List<ManagementObject> servicesToAddList = new List<ManagementObject>();
      foreach (ManagementObject mo in localServicesList)
      {
        if (mo != null)
        {
          servicesToAddList.Add(mo);
        }
      }

      //// If we found some services we will try to add the local machine to the list...
      if (servicesToAddList.Count > 0)
      {
        ChangeMachine(LocalMachine, ChangeType.AutoAdd);

        //// Try to add the services we found on it.
        foreach (ManagementObject mo in servicesToAddList)
        {
          MySQLService service = new MySQLService(mo.Properties["Name"].Value.ToString(), true, true, LocalMachine);
          service.SetServiceParameters();
          LocalMachine.ChangeService(service, ChangeType.AutoAdd);
        }
      }

      Settings.Default.FirstRun = false;
      SavetoFile();
    }

    /// <summary>
    /// Merge the old services schema into the new one
    /// </summary>
    private void MigrateOldServices()
    {
      //// Load old services schema
      MySQLServicesList mySQLServicesList = new MySQLServicesList();

      //// Attempt migration only if services were found
      if (mySQLServicesList.Services != null && mySQLServicesList.Services.Count > 0)
      {
        ChangeMachine(LocalMachine, ChangeType.AutoAdd);

        //// Copy services from old schema to the Local machine
        foreach (MySQLService service in mySQLServicesList.Services)
        {
          LocalMachine.ChangeService(service, ChangeType.AutoAdd);
        }

        //// Clear the old list of services to erase the duplicates on the newer schema
        mySQLServicesList.Services.Clear();
        SavetoFile();
      }
    }
  }

  /// <summary>
  /// Specifies the type of change that a list suffered.
  /// </summary>
  public enum ChangeType
  {
    /// <summary>
    /// An element was added to the list by a user.
    /// </summary>
    AddByUser,

    /// <summary>
    /// An element was added to the list during the initial load.
    /// </summary>
    AddByLoad,

    /// <summary>
    /// An element has been added to the list automatically by an Auto-Add or Service Migration operation.
    /// </summary>
    AutoAdd,

    /// <summary>
    /// All elements in the list have been cleared, list became empty.
    /// </summary>
    Cleared,

    /// <summary>
    /// An element was removed from the list by a user.
    /// </summary>
    RemoveByUser,

    /// <summary>
    /// An element was removed from the list by an event notification.
    /// </summary>
    RemoveByEvent,

    /// <summary>
    /// An element within the list was updated.
    /// </summary>
    Updated
  }
}