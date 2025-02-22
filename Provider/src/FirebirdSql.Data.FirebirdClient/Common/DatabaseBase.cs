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

//$Authors = Jiri Cincura (jiri@cincura.net)

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FirebirdSql.Data.Common
{
	internal abstract class DatabaseBase
	{
		public Action<IscException> WarningMessage { get; set; }

		public abstract int Handle { get; }
		public int TransactionCount { get; set; }
		public string ServerVersion { get; protected set; }
		public Charset Charset { get; set; }
		public short PacketSize { get; set; }
		public short Dialect { get; set; }
		public abstract bool HasRemoteEventSupport { get; }
		public abstract bool ConnectionBroken { get; }

		public abstract ValueTask Attach(DatabaseParameterBufferBase dpb, string database, byte[] cryptKey, AsyncWrappingCommonArgs async);
		public abstract ValueTask AttachWithTrustedAuth(DatabaseParameterBufferBase dpb, string database, byte[] cryptKey, AsyncWrappingCommonArgs async);
		public abstract ValueTask Detach(AsyncWrappingCommonArgs async);

		public abstract ValueTask CreateDatabase(DatabaseParameterBufferBase dpb, string database, byte[] cryptKey, AsyncWrappingCommonArgs async);
		public abstract ValueTask CreateDatabaseWithTrustedAuth(DatabaseParameterBufferBase dpb, string database, byte[] cryptKey, AsyncWrappingCommonArgs async);
		public abstract ValueTask DropDatabase(AsyncWrappingCommonArgs async);

		public abstract ValueTask<TransactionBase> BeginTransaction(TransactionParameterBuffer tpb, AsyncWrappingCommonArgs async);

		public abstract StatementBase CreateStatement();
		public abstract StatementBase CreateStatement(TransactionBase transaction);

		public abstract DatabaseParameterBufferBase CreateDatabaseParameterBuffer();

		public abstract ValueTask<List<object>> GetDatabaseInfo(byte[] items, AsyncWrappingCommonArgs async);
		public abstract ValueTask<List<object>> GetDatabaseInfo(byte[] items, int bufferLength, AsyncWrappingCommonArgs async);

		public abstract ValueTask CloseEventManager(AsyncWrappingCommonArgs async);
		public abstract ValueTask QueueEvents(RemoteEvent events, AsyncWrappingCommonArgs async);
		public abstract ValueTask CancelEvents(RemoteEvent events, AsyncWrappingCommonArgs async);

		public abstract ValueTask CancelOperation(int kind, AsyncWrappingCommonArgs async);

		public async ValueTask<string> GetServerVersion(AsyncWrappingCommonArgs async)
		{
			var items = new byte[]
			{
				IscCodes.isc_info_firebird_version,
				IscCodes.isc_info_end
			};
			var info = await GetDatabaseInfo(items, IscCodes.BUFFER_SIZE_256, async).ConfigureAwait(false);
			return (string)info[info.Count -1];
		}
	}
}
