﻿using MHServerEmu.Common.Logging;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Regions;
using System.Collections;

namespace MHServerEmu.Games.Generators
{
    public class GenCellConnectivityTest
    {
        public class GenCellConnection
        {
            private readonly GenCell _origin;
            private readonly GenCell _target;

            public GenCellConnection(GenCell origin, GenCell target)
            {
                _origin = origin;
                _target = target;
            }

            public bool Test(GenCell cellA, GenCell cellB)
            {
                return cellA == _origin && cellB == _target || cellB == _origin && cellA == _target;
            }
        }

        private readonly Dictionary<GenCell, bool> _connectivity = new();

        public GenCellConnectivityTest() { }

        public bool TestConnectionsRequired(GenCellContainer container, GenCell cell, List<GenCellConnection> list)
        {
            Reset(container);
            RunTreeWithExcludedConnections(cell, list);

            foreach (var item in _connectivity)
                if (!item.Value) return true;

            return false;
        }

        private void RunTreeWithExcludedConnections(GenCell cell, List<GenCellConnection> list)
        {
            if (cell == null) return;
            foreach (GenCell connection in cell.Connections)
            {
                if (connection == null) continue;
                if (!IsConnectionInList(list, cell, connection) && !_connectivity[connection])
                {
                    _connectivity[connection] = true;
                    RunTreeWithExcludedConnections(connection, list);
                }
            }
        }

        private void RunTreeWithExcludedConnection(GenCell cell, GenCell origin, GenCell target)
        {
            if (cell == null || origin == null || target == null) return;

            List<GenCellConnection> list = new()
            {
                new (origin, target)
            };
            RunTreeWithExcludedConnections(cell, list);
        }

        public static bool IsConnectionInList(List<GenCellConnection> list, GenCell cell, GenCell cellConnection)
        {
            foreach (GenCellConnection connection in list)
                if (connection.Test(cell, cellConnection)) return true;

            return false;
        }

        private void Reset(GenCellContainer container)
        {
            _connectivity.Clear();
            foreach (GenCell cell in container)
                if (cell != null) _connectivity[cell] = false;
        }

        public bool TestCellConnected(GenCellContainer container, GenCell cell)
        {
            if (cell.Connections.First() == cell.Connections.Last()) return false;

            Reset(container);
            RunTreeWithExcludedCell(cell, null);

            foreach (var item in _connectivity)
                if (!item.Value) return false;

            return true;
        }

        private void RunTreeWithExcludedCell(GenCell cell, GenCell excludedCell)
        {
            if (cell == null) return; // Internal Generation Error
            foreach (GenCell connection in cell.Connections)
            {
                if (connection == excludedCell) continue;
                if (!_connectivity[connection])
                {
                    _connectivity[connection] = true;
                    RunTreeWithExcludedCell(connection, excludedCell);
                }
            }
        }

        public bool TestCellRequired(GenCellContainer container, GenCell requiredCell)
        {
            if (requiredCell == null) return false;
            Reset(container);

            foreach (GenCell connection in requiredCell.Connections)
            {
                if (connection == null) continue;
                RunTreeWithExcludedCell(connection, requiredCell);
            }

            foreach (var item in _connectivity)
                if (!item.Value && item.Key != requiredCell) return true;

            return false;
        }

        public bool TestConnectionRequired(GenCellContainer container, GenCell cellA, GenCell cellB)
        {
            Reset(container);

            if (cellB.CellRef != 0 || cellA.CellRef != 0) return true;

            RunTreeWithExcludedConnection(cellA, cellA, cellB);

            foreach (var item in _connectivity)
                if (!item.Value) return true;

            return false;
        }
    }

    public class GenCellContainer : IEnumerable<GenCell>
    {
        public static readonly Logger Logger = LogManager.CreateLogger();

        public List<GenCell> StartCells = new();
        public List<GenCell> DestinationCells = new();
        public int DeadEndMax { get; set; }

        public int NumCells { get; private set; }

        public readonly List<GenCell> Cells = new();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public IEnumerator<GenCell> GetEnumerator() => Cells.GetEnumerator();

        public bool CreateCell(uint id, Vector3 position, ulong cellRef)
        {
            GenCell cell = new(id, position, cellRef);
            Cells.Add(cell);
            ++NumCells;
            return true;
        }

        public bool Initialize(int size = 0)
        {
            DestroyAllCells();
            for (int i = 0; i < size; i++)
            {
                Cells.Add(new());
                ++NumCells;
            }
            return true;
        }

        private void DestroyAllCells()
        {
            for (int i = 0; i < Cells.Count; i++)
                DestroyCell(i);

            Cells.Clear();
            StartCells.Clear();
            DestinationCells.Clear();
            NumCells = 0;
        }

        public bool DestroyCell(int index)
        {
            if (index < Cells.Count)
            {
                DestroyCell(Cells[index]);
                Cells[index] = null;
                return true;
            }
            return false;
        }

        private bool DestroyCell(GenCell cell)
        {
            if (cell != null)
            {
                cell.DisconnectFromAll();
                --NumCells;
                return true;
            }
            return false;
        }

        public GenCell GetCell(int index)
        {
            if (index < Cells.Count) return Cells[index];
            return null;
        }

        public bool VerifyIndex(int index)
        {
            return index < Cells.Count;
        }

        public bool DestroyableCell(int index)
        {
            if (index < Cells.Count)
            {
                GenCell cell = Cells[index];
                if (cell != null) return DestroyableCell(cell);
            }
            return false;
        }

        public virtual bool DestroyableCell(GenCell cell)
        {
            if (cell == null || cell.CellRef != 0 || cell.ExternalConnections != Cell.Type.None) return false;

            foreach (var connection in cell.Connections)
                if (connection != null && connection.CellRef != 0) return false;

            if (!CheckForConnectivity(cell)) return false;

            GenCellConnectivityTest test = new();
            return !test.TestCellRequired(this, cell);
        }

        private bool CheckForConnectivity(GenCell checkedCell)
        {
            if (DeadEndMax > 0)
            {
                foreach (GenCell cell in Cells)
                {
                    if (cell == null) continue;
                    int connected = 0;
                    foreach (GenCell connection in cell.Connections)
                        if (connection != checkedCell) connected++;

                    if (cell != null && connected == 1
                        && !CheckForConnectivityPerCell(cell, 1, DeadEndMax, null, checkedCell)) return false;
                }
            }
            return true;
        }

        public bool CheckForConnectivityPerCell(GenCell cell, int level, int maxlevel, GenCell prev, GenCell cellA, GenCell cellB)
        {
            if (cell != null)
            {
                if (level > maxlevel) return false;

                int connections = 0;
                foreach (GenCell connection in cell.Connections)
                {
                    if (!(connection == cellA && cell == cellB
                        || connection == cellB && cell == cellA))
                        connections++;
                }

                if (connections >= 3) return true;

                if (StartCells.Contains(cell) || DestinationCells.Contains(cell)) return true;

                bool check = false;
                foreach (GenCell connection in cell.Connections)
                {
                    if (connection == prev
                        || connection == cellA && cell == cellB
                        || connection == cellB && cell == cellA)
                        continue;

                    check |= CheckForConnectivityPerCell(connection, level++, maxlevel, cell, cellA, cellB);
                }

                return check;
            }

            return false;
        }

        private bool CheckForConnectivityPerCell(GenCell cell, int level, int maxlevel, GenCell prev, GenCell checkedCell)
        {
            if (cell != null)
            {
                if (level > maxlevel) return false;

                int connections = 0;
                foreach (GenCell connection in cell.Connections)
                    if (connection != checkedCell) connections++;

                if (connections >= 3) return true;

                if (StartCells.Contains(cell) || DestinationCells.Contains(cell)) return true;

                bool check = false;
                foreach (GenCell connection in cell.Connections)
                {
                    if (connection == prev || connection == checkedCell) continue;
                    check |= CheckForConnectivityPerCell(connection, level++, maxlevel, cell, checkedCell);
                }

                return check;
            }
            return false;
        }

    }

}
