﻿/*
 *    The contents of this file are subject to the Initial
 *    Developer's Public License Version 1.0 (the "License");
 *    you may not use this file except in compliance with the
 *    License. You may obtain a copy of the License at
 *    https://github.com/FirebirdSQL/NETProvider/blob/master/license.txt.
 *
 *    Software distributed under the License is distributed on
 *    an "AS IS" basis, WITHOUT WARRANTY OF ANY KIND, either
 *    express or implied. See the License for the specific
 *    language governing rights and limitations under the License.
 *
 *    All Rights Reserved.
 */

//$Authors = Carlos Guzman Alvarez, Jiri Cincura (jiri@cincura.net)

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using FirebirdSql.Data.Client.Native.Handle;
using FirebirdSql.Data.Common;

namespace FirebirdSql.Data.Client.Native
{
	internal sealed class FesDatabase : DatabaseBase
	{
		#region Fields

		private DatabaseHandle _handle;
		private IntPtr[] _statusVector;
		private IFbClient _fbClient;

		#endregion

		#region Properties

		public override int Handle
		{
			get { return _handle.DangerousGetHandle().AsInt(); }
		}

		public DatabaseHandle HandlePtr
		{
			get { return _handle; }
		}

		public override bool HasRemoteEventSupport
		{
			get { return false; }
		}

		public override bool ConnectionBroken
		{
			get { return false; }
		}

		public IFbClient FbClient
		{
			get { return _fbClient; }
		}

		#endregion

		#region Constructors

		public FesDatabase(string dllName, Charset charset)
		{
			_fbClient = FbClientFactory.Create(dllName);
			_handle = new DatabaseHandle();
			Charset = charset ?? Charset.DefaultCharset;
			Dialect = 3;
			PacketSize = 8192;
			_statusVector = new IntPtr[IscCodes.ISC_STATUS_LENGTH];
		}

		#endregion

		#region Database Methods

		public override ValueTask CreateDatabase(DatabaseParameterBufferBase dpb, string database, byte[] cryptKey, AsyncWrappingCommonArgs async)
		{
			CheckCryptKeyForSupport(cryptKey);

			var databaseBuffer = Encoding2.Default.GetBytes(database);

			ClearStatusVector();

			_fbClient.isc_create_database(
				_statusVector,
				(short)databaseBuffer.Length,
				databaseBuffer,
				ref _handle,
				dpb.Length,
				dpb.ToArray(),
				0);

			ProcessStatusVector(_statusVector);

			return ValueTask2.CompletedTask;
		}

		public override ValueTask CreateDatabaseWithTrustedAuth(DatabaseParameterBufferBase dpb, string database, byte[] cryptKey, AsyncWrappingCommonArgs async)
		{
			throw new NotSupportedException("Trusted Auth isn't supported on Firebird Embedded.");
		}

		public override ValueTask DropDatabase(AsyncWrappingCommonArgs async)
		{
			ClearStatusVector();

			_fbClient.isc_drop_database(_statusVector, ref _handle);

			ProcessStatusVector(_statusVector);

			_handle.Dispose();

			return ValueTask2.CompletedTask;
		}

		#endregion

		#region Remote Events Methods

		public override ValueTask CloseEventManager(AsyncWrappingCommonArgs async)
		{
			throw new NotSupportedException();
		}

		public override ValueTask QueueEvents(RemoteEvent events, AsyncWrappingCommonArgs async)
		{
			throw new NotSupportedException();
		}

		public override ValueTask CancelEvents(RemoteEvent events, AsyncWrappingCommonArgs async)
		{
			throw new NotSupportedException();
		}

		#endregion

		#region Methods

		public override async ValueTask Attach(DatabaseParameterBufferBase dpb, string database, byte[] cryptKey, AsyncWrappingCommonArgs async)
		{
			CheckCryptKeyForSupport(cryptKey);

			var databaseBuffer = Encoding2.Default.GetBytes(database);

			ClearStatusVector();

			_fbClient.isc_attach_database(
				_statusVector,
				(short)databaseBuffer.Length,
				databaseBuffer,
				ref _handle,
				dpb.Length,
				dpb.ToArray());

			ProcessStatusVector(_statusVector);

			ServerVersion = await GetServerVersion(async).ConfigureAwait(false);
		}

		public override ValueTask AttachWithTrustedAuth(DatabaseParameterBufferBase dpb, string database, byte[] cryptKey, AsyncWrappingCommonArgs async)
		{
			throw new NotSupportedException("Trusted Auth isn't supported on Firebird Embedded.");
		}

		public override ValueTask Detach(AsyncWrappingCommonArgs async)
		{
			if (TransactionCount > 0)
			{
				throw IscException.ForErrorCodeIntParam(IscCodes.isc_open_trans, TransactionCount);
			}

			if (!_handle.IsInvalid)
			{
				ClearStatusVector();

				_fbClient.isc_detach_database(_statusVector, ref _handle);

				ProcessStatusVector(_statusVector);

				_handle.Dispose();
			}

			WarningMessage = null;
			Charset = null;
			ServerVersion = null;
			_statusVector = null;
			TransactionCount = 0;
			Dialect = 0;
			PacketSize = 0;

			return ValueTask2.CompletedTask;
		}

		#endregion

		#region Transaction Methods

		public override async ValueTask<TransactionBase> BeginTransaction(TransactionParameterBuffer tpb, AsyncWrappingCommonArgs async)
		{
			var transaction = new FesTransaction(this);
			await transaction.BeginTransaction(tpb, async).ConfigureAwait(false);
			return transaction;
		}

		#endregion

		#region Cancel Methods

		public override ValueTask CancelOperation(int kind, AsyncWrappingCommonArgs async)
		{
			var localStatusVector = new IntPtr[IscCodes.ISC_STATUS_LENGTH];

			_fbClient.fb_cancel_operation(localStatusVector, ref _handle, kind);

			try
			{
				ProcessStatusVector(localStatusVector);
			}
			catch (IscException ex) when (ex.ErrorCode == IscCodes.isc_nothing_to_cancel)
			{ }

			return ValueTask2.CompletedTask;
		}

		#endregion

		#region Statement Creation Methods

		public override StatementBase CreateStatement()
		{
			return new FesStatement(this);
		}

		public override StatementBase CreateStatement(TransactionBase transaction)
		{
			return new FesStatement(this, transaction as FesTransaction);
		}

		#endregion

		#region DPB

		public override DatabaseParameterBufferBase CreateDatabaseParameterBuffer()
		{
			return new DatabaseParameterBuffer1();
		}

		#endregion

		#region Database Information Methods

		public override ValueTask<List<object>> GetDatabaseInfo(byte[] items, AsyncWrappingCommonArgs async)
		{
			return GetDatabaseInfo(items, IscCodes.DEFAULT_MAX_BUFFER_SIZE, async);
		}

		public override ValueTask<List<object>> GetDatabaseInfo(byte[] items, int bufferLength, AsyncWrappingCommonArgs async)
		{
			var buffer = new byte[bufferLength];

			DatabaseInfo(items, buffer, buffer.Length);

			return ValueTask2.FromResult(IscHelper.ParseDatabaseInfo(buffer));
		}

		#endregion

		#region Internal Methods

		internal void ProcessStatusVector(IntPtr[] statusVector)
		{
			var ex = FesConnection.ParseStatusVector(statusVector, Charset);

			if (ex != null)
			{
				if (ex.IsWarning)
				{
					WarningMessage?.Invoke(ex);
				}
				else
				{
					throw ex;
				}
			}
		}

		#endregion

		#region Private Methods

		private void ClearStatusVector()
		{
			Array.Clear(_statusVector, 0, _statusVector.Length);
		}

		private void DatabaseInfo(byte[] items, byte[] buffer, int bufferLength)
		{
			ClearStatusVector();

			_fbClient.isc_database_info(
				_statusVector,
				ref _handle,
				(short)items.Length,
				items,
				(short)bufferLength,
				buffer);

			ProcessStatusVector(_statusVector);
		}

		#endregion

		#region Internal Static Methods

		internal static void CheckCryptKeyForSupport(byte[] cryptKey)
		{
			// ICryptKeyCallbackImpl would have to be passed from C# for 'cryptKey' passing
			if (cryptKey?.Length > 0)
				throw new NotSupportedException("Passing Encryption Key isn't, yet, supported on Firebird Embedded.");
		}

		#endregion
	}
}
