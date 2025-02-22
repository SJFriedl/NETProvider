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

//$Authors = Hajime Nakagami, Jiri Cincura (jiri@cincura.net)

using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FirebirdSql.Data.Common;

namespace FirebirdSql.Data.Client.Managed.Version13
{
	internal class GdsDatabase : Version12.GdsDatabase
	{
		public GdsDatabase(GdsConnection connection)
			: base(connection)
		{ }

		public override async ValueTask Attach(DatabaseParameterBufferBase dpb, string database, byte[] cryptKey, AsyncWrappingCommonArgs async)
		{
			try
			{
				await SendAttachToBuffer(dpb, database, async).ConfigureAwait(false);
				await Xdr.Flush(async).ConfigureAwait(false);
				var response = await ReadResponse(async).ConfigureAwait(false);
				if (response is ContAuthResponse)
				{
					while (response is ContAuthResponse contAuthResponse)
					{
						AuthBlock.Start(contAuthResponse.ServerData, contAuthResponse.AcceptPluginName, contAuthResponse.IsAuthenticated, contAuthResponse.ServerKeys);

						await AuthBlock.SendContAuthToBuffer(Xdr, async).ConfigureAwait(false);
						await Xdr.Flush(async).ConfigureAwait(false);
						response = await AuthBlock.ProcessContAuthResponse(Xdr, async).ConfigureAwait(false);
						response = await ProcessCryptCallbackResponseIfNeeded(response, cryptKey, async).ConfigureAwait(false);
					}
					var genericResponse = (GenericResponse)response;
					await base.ProcessAttachResponse(genericResponse, async).ConfigureAwait(false);

					if (genericResponse.Data.Any())
					{
						await AuthBlock.SendWireCryptToBuffer(Xdr, async).ConfigureAwait(false);
						await Xdr.Flush(async).ConfigureAwait(false);
						await AuthBlock.ProcessWireCryptResponse(Xdr, _connection, async).ConfigureAwait(false);
					}
				}
				else
				{
					response = await ProcessCryptCallbackResponseIfNeeded(response, cryptKey, async).ConfigureAwait(false);
					await ProcessAttachResponse((GenericResponse)response, async).ConfigureAwait(false);
					AuthBlock.Complete();
				}
				AuthBlock.WireCryptValidate(IscCodes.PROTOCOL_VERSION13);
			}
			catch (IscException)
			{
				await SafelyDetach(async).ConfigureAwait(false);
				throw;
			}
			catch (IOException ex)
			{
				await SafelyDetach(async).ConfigureAwait(false);
				throw IscException.ForIOException(ex);
			}

			await AfterAttachActions(async).ConfigureAwait(false);
		}

		protected override async ValueTask SendAttachToBuffer(DatabaseParameterBufferBase dpb, string database, AsyncWrappingCommonArgs async)
		{
			await Xdr.Write(IscCodes.op_attach, async).ConfigureAwait(false);
			await Xdr.Write(0, async).ConfigureAwait(false);
			if (!AuthBlock.HasClientData)
			{
				dpb.Append(IscCodes.isc_dpb_auth_plugin_name, AuthBlock.AcceptPluginName);
				dpb.Append(IscCodes.isc_dpb_specific_auth_data, AuthBlock.PublicClientData);
			}
			else
			{
				dpb.Append(IscCodes.isc_dpb_specific_auth_data, AuthBlock.ClientData);
			}
			dpb.Append(IscCodes.isc_dpb_utf8_filename, 0);
			await Xdr.WriteBuffer(Encoding.UTF8.GetBytes(database), async).ConfigureAwait(false);
			await Xdr.WriteBuffer(dpb.ToArray(), async).ConfigureAwait(false);
		}

		public override async ValueTask CreateDatabase(DatabaseParameterBufferBase dpb, string database, byte[] cryptKey, AsyncWrappingCommonArgs async)
		{
			try
			{
				await SendCreateToBuffer(dpb, database, async).ConfigureAwait(false);
				await Xdr.Flush(async).ConfigureAwait(false);
				var response = await ReadResponse(async).ConfigureAwait(false);
				if (response is ContAuthResponse)
				{
					while (response is ContAuthResponse contAuthResponse)
					{
						AuthBlock.Start(contAuthResponse.ServerData, contAuthResponse.AcceptPluginName, contAuthResponse.IsAuthenticated, contAuthResponse.ServerKeys);

						await AuthBlock.SendContAuthToBuffer(Xdr, async).ConfigureAwait(false);
						await Xdr.Flush(async).ConfigureAwait(false);
						response = await AuthBlock.ProcessContAuthResponse(Xdr, async).ConfigureAwait(false);
						response = await ProcessCryptCallbackResponseIfNeeded(response, cryptKey, async).ConfigureAwait(false);
					}
					var genericResponse = (GenericResponse)response;
					await ProcessCreateResponse(genericResponse, async).ConfigureAwait(false);

					if (genericResponse.Data.Any())
					{
						await AuthBlock.SendWireCryptToBuffer(Xdr, async).ConfigureAwait(false);
						await Xdr.Flush(async).ConfigureAwait(false);
						await AuthBlock.ProcessWireCryptResponse(Xdr, _connection, async).ConfigureAwait(false);
					}
				}
				else
				{
					response = await ProcessCryptCallbackResponseIfNeeded(response, cryptKey, async).ConfigureAwait(false);
					await ProcessCreateResponse((GenericResponse)response, async).ConfigureAwait(false);
					AuthBlock.Complete();
				}
			}
			catch (IOException ex)
			{
				throw IscException.ForIOException(ex);
			}
		}

		protected override async ValueTask SendCreateToBuffer(DatabaseParameterBufferBase dpb, string database, AsyncWrappingCommonArgs async)
		{
			await Xdr.Write(IscCodes.op_create, async).ConfigureAwait(false);
			await Xdr.Write(0, async).ConfigureAwait(false);
			if (!AuthBlock.HasClientData)
			{
				dpb.Append(IscCodes.isc_dpb_auth_plugin_name, AuthBlock.AcceptPluginName);
				dpb.Append(IscCodes.isc_dpb_specific_auth_data, AuthBlock.PublicClientData);
			}
			else
			{
				dpb.Append(IscCodes.isc_dpb_specific_auth_data, AuthBlock.ClientData);
			}
			dpb.Append(IscCodes.isc_dpb_utf8_filename, 0);
			await Xdr.WriteBuffer(Encoding.UTF8.GetBytes(database), async).ConfigureAwait(false);
			await Xdr.WriteBuffer(dpb.ToArray(), async).ConfigureAwait(false);
		}

		public override ValueTask AttachWithTrustedAuth(DatabaseParameterBufferBase dpb, string database, byte[] cryptKey, AsyncWrappingCommonArgs async)
		{
			return Attach(dpb, database, cryptKey, async);
		}

		public override ValueTask CreateDatabaseWithTrustedAuth(DatabaseParameterBufferBase dpb, string database, byte[] cryptKey, AsyncWrappingCommonArgs async)
		{
			return CreateDatabase(dpb, database, cryptKey, async);
		}

		internal async ValueTask<IResponse> ProcessCryptCallbackResponseIfNeeded(IResponse response, byte[] cryptKey, AsyncWrappingCommonArgs async)
		{
			while (response is CryptKeyCallbackResponse)
			{
				await Xdr.Write(IscCodes.op_crypt_key_callback, async).ConfigureAwait(false);
				await Xdr.WriteBuffer(cryptKey, async).ConfigureAwait(false);
				await Xdr.Flush(async).ConfigureAwait(false);
				response = await ReadResponse(async).ConfigureAwait(false);
			}
			return response;
		}

		public override StatementBase CreateStatement()
		{
			return new GdsStatement(this);
		}

		public override StatementBase CreateStatement(TransactionBase transaction)
		{
			return new GdsStatement(this, transaction);
		}

		public override DatabaseParameterBufferBase CreateDatabaseParameterBuffer()
		{
			return new DatabaseParameterBuffer2();
		}
	}
}
