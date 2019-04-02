using System;
using System.Data;
using System.Data.Common;
using System.Collections.Generic;

using Mighty.Plugins;
using System.Threading.Tasks;
using System.Threading;

namespace Mighty.Npgsql
{
	// Cursor dereferencing data reader derived originally from removed Npgsql code (but now with more consistent behaviour), might go back into Npgsql at some point?
	// Note that Oracle basically does the equivalent of this in the driver.
	internal class NpgsqlDereferencingReader : DbDataReader, IDisposable
	{
		private DbConnection Connection;
		private dynamic Mighty;
		private int FetchSize;

		private DbDataReader fetchReader = null; // current FETCH reader
		private List<string> Cursors = new List<string>();
		private int Index = 0;
		private string Cursor = null;
		private int Count; // # read on current FETCH

		private DbDataReader originalReader;
		private CommandBehavior Behavior;

		/// <summary>
		/// Create a safe, sensible dereferencing reader; we have already checked that there are at least some cursors to dereference at this point.
		/// </summary>
		/// <param name="reader">The original reader for the undereferenced query.</param>
		/// <param name="behavior">The required <see cref="CommandBehavior"/></param>
		/// <param name="connection">The connection to use.</param>
		/// <param name="mighty">The owning Mighty instance.</param>
		/// <param name="cancellationToken">Async <see cref="CancellationToken"/>.</param>
		/// <remarks>
		/// FETCH ALL is genuinely useful in some situations (e.g. if using (abusing?) cursors to return small or medium sized multiple result
		/// sets then we can and do save one round trip to the database overall: n cursors round trips, rather than n cursors plus one), but since
		/// it is badly problematic in the case of large cursors we force the user to request it explicitly.
		/// https://github.com/npgsql/npgsql/issues/438
		/// http://stackoverflow.com/questions/42292341/
		/// </remarks>
		public NpgsqlDereferencingReader(DbDataReader reader, CommandBehavior behavior, DbConnection connection, dynamic mighty)
		{
			FetchSize = mighty.NpgsqlAutoDereferenceFetchSize;
			Connection = connection;
			Mighty = mighty;
			Behavior = behavior;
			originalReader = reader;
		}

		/// <summary>
		/// Initialise the reader
		/// </summary>
		/// <returns></returns>
		public async Task InitAsync(CancellationToken cancellationToken)
		{
			// Behavior is only saved to be used here in this Init method; we don't need to check or enforce it again
			// elsewhere since the logic below is already enforcing it.
			// For SingleRow we additionally rely on the user to only read one row and then dispose of everything.
			bool earlyQuit = (Behavior == CommandBehavior.SingleResult || Behavior == CommandBehavior.SingleRow);

			// we're saving all the cursors from the original reader here, then disposing of it
			// (we've already checked in CanDereference that there are at least some cursors)
			using (originalReader)
			{
				// Supports 1x1 1xN Nx1 and NXM patterns of cursor data.
				// If just some values are cursors we follow the pre-existing pattern set by the Oracle drivers, and dereference what we can.
				while (await originalReader.ReadAsync(cancellationToken))
				{
					for (int i = 0; i < originalReader.FieldCount; i++)
					{
						if (originalReader.GetDataTypeName(i) == "refcursor")
						{
							// cursor name can potentially contain " so stop that breaking us
							// TO DO: document how/why
							Cursors.Add(originalReader.GetString(i).Replace(@"""", @""""""));
							if (earlyQuit) break;
						}
					}
					if (earlyQuit) break;
				}
			}

			// initialize
			await NextResultAsync(cancellationToken);
		}

		/// <summary>
		/// True iff current reader has cursors in its output types.
		/// </summary>
		/// <param name="reader">The reader to check</param>
		/// <returns>Are there cursors?</returns>
		/// <remarks>Really a part of NpgsqlDereferencingReader</remarks>
		static public bool CanDereference(DbDataReader reader)
		{
			bool hasCursors = false;
			for (int i = 0; i < reader.FieldCount; i++)
			{
				if (reader.GetDataTypeName(i) == "refcursor")
				{
					hasCursors = true;
					break;
				}
			}
			return hasCursors;
		}

		/// <summary>
		/// SQL to fetch required count from current cursor
		/// </summary>
		/// <returns>SQL</returns>
		private string FetchSQL()
		{
			return string.Format(@"FETCH {0} FROM ""{1}"";", (FetchSize <= 0 ? "ALL" : FetchSize.ToString()), Cursor);
		}

		/// <summary>
		/// SQL to close current cursor
		/// </summary>
		/// <returns>SQL</returns>
		private string CloseSQL()
		{
			return string.Format(@"CLOSE ""{0}"";", Cursor);
		}

		/// <summary>
		/// Close current FETCH cursor on the database
		/// </summary>
		/// <param name="ExecuteNow">Iff false then return the SQL but don't execute the command</param>
		/// <returns>The SQL to close the cursor, if there is one and this has not already been executed.</returns>
		private string CloseCursor(bool ExecuteNow = true)
		{
			// close and dispose current fetch reader for this cursor
			if (fetchReader != null && !fetchReader.IsClosed)
			{
				fetchReader.Dispose();
			}
			// close cursor itself
			if (FetchSize > 0 && !string.IsNullOrEmpty(Cursor))
			{
				var closeSql = CloseSQL();
				if (!ExecuteNow)
				{
					return closeSql;
				}
				var closeCmd = CreateCommand(closeSql, Connection); // new NpgsqlCommand(..., Connection);
				closeCmd.ExecuteNonQuery();
				closeCmd.Dispose();
				Cursor = null;
			}
			return "";
		}

		/// <summary>
		/// Fetch next N rows from current cursor.
		/// </summary>
		/// <param name="cancellationToken">Async <see cref="CancellationToken"/>.</param>
		/// <param name="closePreviousSQL">SQL to prepend, to close the previous cursor in a single round trip (optional).</param>
		private async Task FetchNextNFromCursorAsync(CancellationToken cancellationToken, string closePreviousSQL = "")
		{
			// close and dispose previous fetch reader for this cursor
			if (fetchReader != null && !fetchReader.IsClosed)
			{
				fetchReader.Dispose();
			}
			// fetch next n from cursor;
			// optionally close previous cursor;
			// iff we're fetching all, we can close this cursor in this command
			var fetchCmd = CreateCommand(closePreviousSQL + FetchSQL() + (FetchSize <= 0 ? CloseSQL() : ""), Connection); // new NpgsqlCommand(..., Connection);

			// NpgsqlCommand.ExecuteReaderAsync is ignoring its CancellationToken
			if (cancellationToken.IsCancellationRequested)
			{
				throw new TaskCanceledException();
			}

			fetchReader = await fetchCmd.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);
			Count = 0;
		}

		private DbCommand CreateCommand(string sql, DbConnection connection)
		{
			// cast only needed because we're storing Mighty in a dynamic (to avoid a generic typing nightmare)
			var command = (DbCommand)Mighty.CreateCommand(sql);
			command.Connection = connection;
			return command;
		}

		#region DbDataReader abstract interface
		public override object this[string name] { get { return fetchReader[name]; } }
		public override object this[int i] { get { return fetchReader[i]; } }
		public override int Depth { get { return fetchReader.Depth; } }
		public override int FieldCount { get { return fetchReader.FieldCount; } }
		public override bool HasRows { get { return fetchReader.HasRows; } }
		public override bool IsClosed { get { return fetchReader.IsClosed; } }
		public override int RecordsAffected { get { return fetchReader.RecordsAffected; } }

#if NETFRAMEWORK
		public override void Close()
		{
			CloseCursor();
		}
#endif

		public override bool GetBoolean(int i) { return fetchReader.GetBoolean(i); }
		public override byte GetByte(int i) { return fetchReader.GetByte(i); }
		public override long GetBytes(int i, long fieldOffset, byte[] buffer, int bufferoffset, int length) { return fetchReader.GetBytes(i, fieldOffset, buffer, bufferoffset, length); }
		public override char GetChar(int i) { return fetchReader.GetChar(i); }
		public override long GetChars(int i, long fieldoffset, char[] buffer, int bufferoffset, int length) { return fetchReader.GetChars(i, fieldoffset, buffer, bufferoffset, length); }
		//public IDataReader GetData(int i) { return Reader.GetData(i); }
		public override string GetDataTypeName(int i) { return fetchReader.GetDataTypeName(i); }
		public override DateTime GetDateTime(int i) { return fetchReader.GetDateTime(i); }
		public override decimal GetDecimal(int i) { return fetchReader.GetDecimal(i); }
		public override double GetDouble(int i) { return fetchReader.GetDouble(i); }

		public override System.Collections.IEnumerator GetEnumerator() { throw new NotSupportedException(); }

		public override Type GetFieldType(int i) { return fetchReader.GetFieldType(i); }
		public override float GetFloat(int i) { return fetchReader.GetFloat(i); }
		public override Guid GetGuid(int i) { return fetchReader.GetGuid(i); }
		public override short GetInt16(int i) { return fetchReader.GetInt16(i); }
		public override int GetInt32(int i) { return fetchReader.GetInt32(i); }
		public override long GetInt64(int i) { return fetchReader.GetInt64(i); }
		public override string GetName(int i) { return fetchReader.GetName(i); }
#if NETFRAMEWORK
		public override DataTable GetSchemaTable() { return fetchReader.GetSchemaTable(); }
#endif
		public override int GetOrdinal(string name) { return fetchReader.GetOrdinal(name); }
		public override string GetString(int i) { return fetchReader.GetString(i); }
		public override object GetValue(int i) { return fetchReader.GetValue(i); }
		public override int GetValues(object[] values) { return fetchReader.GetValues(values); }
		public override bool IsDBNull(int i) { return fetchReader.IsDBNull(i); }

		public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
		{
			var closeSql = CloseCursor(Index >= Cursors.Count);
			if (Index >= Cursors.Count)
			{
				return false;
			}
			Cursor = Cursors[Index++];
			await FetchNextNFromCursorAsync(cancellationToken, closeSql);
			return true;
		}

		public override bool NextResult()
		{
			throw new NotImplementedException($"{nameof(NpgsqlDereferencingReader)}.NextResult synchronous version");
		}

		public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
		{
			if (fetchReader != null)
			{
				// NpgsqlDataReader.ReadAsync is ignoring its CancellationToken
				// TO DO: Test that this is true by showing that the cancellation test fails on Npgsql
				if (cancellationToken.IsCancellationRequested)
				{
					throw new TaskCanceledException();
				}
				bool cursorHasNextRow = await fetchReader.ReadAsync(cancellationToken);
				if (cursorHasNextRow)
				{
					Count++;
					return true;
				}
				// if we did FETCH ALL or if rows expired before requested count, there is nothing more to fetch on this cursor
				if (FetchSize <= 0 || Count < FetchSize)
				{
					return false;
				}
			}
			// if rows expired at requested count, there may or may not be more rows
			await FetchNextNFromCursorAsync(cancellationToken);
			// recursive self-call
			return await ReadAsync(cancellationToken);
		}

		public override bool Read()
		{
			throw new NotImplementedException($"{nameof(NpgsqlDereferencingReader)}.Read synchronous version");
		}
		#endregion

		public new void Dispose()
		{
			CloseCursor();
			base.Dispose();
		}
	}
}