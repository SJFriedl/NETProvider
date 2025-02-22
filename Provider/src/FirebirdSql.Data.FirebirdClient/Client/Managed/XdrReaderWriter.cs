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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using FirebirdSql.Data.Common;
using FirebirdSql.Data.Types;

namespace FirebirdSql.Data.Client.Managed
{
	sealed class XdrReaderWriter : IXdrReader, IXdrWriter
	{
		readonly IDataProvider _dataProvider;
		readonly Charset _charset;

		byte[] _smallBuffer;

		public XdrReaderWriter(IDataProvider dataProvider, Charset charset)
		{
			_dataProvider = dataProvider;
			_charset = charset;

			_smallBuffer = new byte[8];
		}

		public XdrReaderWriter(IDataProvider dataProvider)
			: this(dataProvider, Charset.DefaultCharset)
		{ }

		#region Read

		public async ValueTask<byte[]> ReadBytes(byte[] buffer, int count, AsyncWrappingCommonArgs async)
		{
			if (count > 0)
			{
				var toRead = count;
				var currentlyRead = -1;
				while (toRead > 0 && currentlyRead != 0)
				{
					toRead -= (currentlyRead = await _dataProvider.Read(buffer, count - toRead, toRead, async).ConfigureAwait(false));
				}
				if (currentlyRead == 0)
				{
					if (_dataProvider is ITracksIOFailure tracksIOFailure)
					{
						tracksIOFailure.IOFailed = true;
					}
					throw new IOException($"Missing {toRead} bytes to fill total {count}.");
				}
			}
			return buffer;
		}

		public async ValueTask<byte[]> ReadOpaque(int length, AsyncWrappingCommonArgs async)
		{
			var buffer = new byte[length];
			await ReadBytes(buffer, length, async).ConfigureAwait(false);
			await ReadPad((4 - length) & 3, async).ConfigureAwait(false);
			return buffer;
		}

		public async ValueTask<byte[]> ReadBuffer(AsyncWrappingCommonArgs async)
		{
			return await ReadOpaque((ushort)await ReadInt32(async).ConfigureAwait(false), async).ConfigureAwait(false);
		}

		public ValueTask<string> ReadString(AsyncWrappingCommonArgs async) => ReadString(_charset, async);
		public ValueTask<string> ReadString(int length, AsyncWrappingCommonArgs async) => ReadString(_charset, length, async);
		public async ValueTask<string> ReadString(Charset charset, AsyncWrappingCommonArgs async) => await ReadString(charset, await ReadInt32(async).ConfigureAwait(false), async).ConfigureAwait(false);
		public async ValueTask<string> ReadString(Charset charset, int length, AsyncWrappingCommonArgs async)
		{
			var buffer = await ReadOpaque(length, async).ConfigureAwait(false);
			return charset.GetString(buffer, 0, buffer.Length);
		}

		public async ValueTask<short> ReadInt16(AsyncWrappingCommonArgs async)
		{
			return Convert.ToInt16(await ReadInt32(async).ConfigureAwait(false));
		}

		public async ValueTask<int> ReadInt32(AsyncWrappingCommonArgs async)
		{
			await ReadBytes(_smallBuffer, 4, async).ConfigureAwait(false);
			return TypeDecoder.DecodeInt32(_smallBuffer);
		}

		public async ValueTask<long> ReadInt64(AsyncWrappingCommonArgs async)
		{
			await ReadBytes(_smallBuffer, 8, async).ConfigureAwait(false);
			return TypeDecoder.DecodeInt64(_smallBuffer);
		}

		public async ValueTask<Guid> ReadGuid(AsyncWrappingCommonArgs async)
		{
			return TypeDecoder.DecodeGuid(await ReadOpaque(16, async).ConfigureAwait(false));
		}

		public async ValueTask<float> ReadSingle(AsyncWrappingCommonArgs async)
		{
			return BitConverter.ToSingle(BitConverter.GetBytes(await ReadInt32(async).ConfigureAwait(false)), 0);
		}

		public async ValueTask<double> ReadDouble(AsyncWrappingCommonArgs async)
		{
			return BitConverter.ToDouble(BitConverter.GetBytes(await ReadInt64(async).ConfigureAwait(false)), 0);
		}

		public async ValueTask<DateTime> ReadDateTime(AsyncWrappingCommonArgs async)
		{
			var date = await ReadDate(async).ConfigureAwait(false);
			var time = await ReadTime(async).ConfigureAwait(false);
			return date.Add(time);
		}

		public async ValueTask<DateTime> ReadDate(AsyncWrappingCommonArgs async)
		{
			return TypeDecoder.DecodeDate(await ReadInt32(async).ConfigureAwait(false));
		}

		public async ValueTask<TimeSpan> ReadTime(AsyncWrappingCommonArgs async)
		{
			return TypeDecoder.DecodeTime(await ReadInt32(async).ConfigureAwait(false));
		}

		public async ValueTask<decimal> ReadDecimal(int type, int scale, AsyncWrappingCommonArgs async)
		{
			switch (type & ~1)
			{
				case IscCodes.SQL_SHORT:
					return TypeDecoder.DecodeDecimal(await ReadInt16(async).ConfigureAwait(false), scale, type);
				case IscCodes.SQL_LONG:
					return TypeDecoder.DecodeDecimal(await ReadInt32(async).ConfigureAwait(false), scale, type);
				case IscCodes.SQL_QUAD:
				case IscCodes.SQL_INT64:
					return TypeDecoder.DecodeDecimal(await ReadInt64(async).ConfigureAwait(false), scale, type);
				case IscCodes.SQL_DOUBLE:
				case IscCodes.SQL_D_FLOAT:
					return TypeDecoder.DecodeDecimal(await ReadDouble(async).ConfigureAwait(false), scale, type);
				case IscCodes.SQL_INT128:
					return TypeDecoder.DecodeDecimal(await ReadInt128(async).ConfigureAwait(false), scale, type);
				default:
					throw new ArgumentOutOfRangeException(nameof(type), $"{nameof(type)}={type}");
			}
		}

		public async ValueTask<bool> ReadBoolean(AsyncWrappingCommonArgs async)
		{
			return TypeDecoder.DecodeBoolean(await ReadOpaque(1, async).ConfigureAwait(false));
		}

		public async ValueTask<FbZonedDateTime> ReadZonedDateTime(bool isExtended, AsyncWrappingCommonArgs async)
		{
			var dt = await ReadDateTime(async).ConfigureAwait(false);
			dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
			return TypeHelper.CreateZonedDateTime(dt, (ushort)await ReadInt16(async).ConfigureAwait(false), isExtended ? await ReadInt16(async).ConfigureAwait(false) : (short?)null);
		}

		public async ValueTask<FbZonedTime> ReadZonedTime(bool isExtended, AsyncWrappingCommonArgs async)
		{
			return TypeHelper.CreateZonedTime(await ReadTime(async).ConfigureAwait(false), (ushort)await ReadInt16(async).ConfigureAwait(false), isExtended ? await ReadInt16(async).ConfigureAwait(false) : (short?)null);
		}

		public async ValueTask<FbDecFloat> ReadDec16(AsyncWrappingCommonArgs async)
		{
			return TypeDecoder.DecodeDec16(await ReadOpaque(8, async).ConfigureAwait(false));
		}

		public async ValueTask<FbDecFloat> ReadDec34(AsyncWrappingCommonArgs async)
		{
			return TypeDecoder.DecodeDec34(await ReadOpaque(16, async).ConfigureAwait(false));
		}

		public async ValueTask<BigInteger> ReadInt128(AsyncWrappingCommonArgs async)
		{
			return TypeDecoder.DecodeInt128(await ReadOpaque(16, async).ConfigureAwait(false));
		}

		public async ValueTask<IscException> ReadStatusVector(AsyncWrappingCommonArgs async)
		{
			IscException exception = null;
			var eof = false;

			while (!eof)
			{
				var arg = await ReadInt32(async).ConfigureAwait(false);

				switch (arg)
				{
					case IscCodes.isc_arg_gds:
					default:
						var er = await ReadInt32(async).ConfigureAwait(false);
						if (er != 0)
						{
							if (exception == null)
							{
								exception = IscException.ForBuilding();
							}
							exception.Errors.Add(new IscError(arg, er));
						}
						break;

					case IscCodes.isc_arg_end:
						exception?.BuildExceptionData();
						eof = true;
						break;

					case IscCodes.isc_arg_interpreted:
					case IscCodes.isc_arg_string:
					case IscCodes.isc_arg_sql_state:
						exception.Errors.Add(new IscError(arg, await ReadString(async).ConfigureAwait(false)));
						break;

					case IscCodes.isc_arg_number:
						exception.Errors.Add(new IscError(arg, await ReadInt32(async).ConfigureAwait(false)));
						break;
				}
			}

			return exception;
		}

		/* loop	as long	as we are receiving	dummy packets, just
		 * throwing	them away--note	that if	we are a server	we won't
		 * be receiving	them, but it is	better to check	for	them at
		 * this	level rather than try to catch them	in all places where
		 * this	routine	is called
		 */
		public async ValueTask<int> ReadOperation(AsyncWrappingCommonArgs async)
		{
			int operation;
			do
			{
				operation = await ReadInt32(async).ConfigureAwait(false);
			} while (operation == IscCodes.op_dummy);
			return operation;
		}

		#endregion

		#region Write

		public ValueTask Flush(AsyncWrappingCommonArgs async) => _dataProvider.Flush(async);

		public ValueTask WriteBytes(byte[] buffer, int count, AsyncWrappingCommonArgs async) => _dataProvider.Write(buffer, 0, count, async);

		public ValueTask WriteOpaque(byte[] buffer, AsyncWrappingCommonArgs async) => WriteOpaque(buffer, buffer.Length, async);
		public async ValueTask WriteOpaque(byte[] buffer, int length, AsyncWrappingCommonArgs async)
		{
			if (buffer != null && length > 0)
			{
				await _dataProvider.Write(buffer, 0, buffer.Length, async).ConfigureAwait(false);
				await WriteFill(length - buffer.Length, async).ConfigureAwait(false);
				await WritePad((4 - length) & 3, async).ConfigureAwait(false);
			}
		}

		public ValueTask WriteBuffer(byte[] buffer, AsyncWrappingCommonArgs async) => WriteBuffer(buffer, buffer?.Length ?? 0, async);
		public async ValueTask WriteBuffer(byte[] buffer, int length, AsyncWrappingCommonArgs async)
		{
			await Write(length, async).ConfigureAwait(false);
			if (buffer != null && length > 0)
			{
				await _dataProvider.Write(buffer, 0, length, async).ConfigureAwait(false);
				await WritePad((4 - length) & 3, async).ConfigureAwait(false);
			}
		}

		public async ValueTask WriteBlobBuffer(byte[] buffer, AsyncWrappingCommonArgs async)
		{
			var length = buffer.Length; // 2 for short for buffer length
			if (length > short.MaxValue)
				throw new IOException("Blob buffer too big.");
			await Write(length + 2, async).ConfigureAwait(false);
			await Write(length + 2, async).ConfigureAwait(false);  //bizarre but true! three copies of the length
			await _dataProvider.Write(new[] { (byte)((length >> 0) & 0xff), (byte)((length >> 8) & 0xff) }, 0, 2, async).ConfigureAwait(false);
			await _dataProvider.Write(buffer, 0, length, async).ConfigureAwait(false);
			await WritePad((4 - length + 2) & 3, async).ConfigureAwait(false);
		}

		public async ValueTask WriteTyped(int type, byte[] buffer, AsyncWrappingCommonArgs async)
		{
			int length;
			if (buffer == null)
			{
				await Write(1, async).ConfigureAwait(false);
				await _dataProvider.Write(new[] { (byte)type }, 0, 1, async).ConfigureAwait(false);
				length = 1;
			}
			else
			{
				length = buffer.Length + 1;
				await Write(length, async).ConfigureAwait(false);
				await _dataProvider.Write(new[] { (byte)type }, 0, 1, async).ConfigureAwait(false);
				await _dataProvider.Write(buffer, 0, buffer.Length, async).ConfigureAwait(false);
			}
			await WritePad((4 - length) & 3, async).ConfigureAwait(false);
		}

		public ValueTask Write(string value, AsyncWrappingCommonArgs async)
		{
			var buffer = _charset.GetBytes(value);
			return WriteBuffer(buffer, buffer.Length, async);
		}

		public ValueTask Write(short value, AsyncWrappingCommonArgs async)
		{
			return Write((int)value, async);
		}

		public ValueTask Write(int value, AsyncWrappingCommonArgs async)
		{
			return _dataProvider.Write(TypeEncoder.EncodeInt32(value), 0, 4, async);
		}

		public ValueTask Write(long value, AsyncWrappingCommonArgs async)
		{
			return _dataProvider.Write(TypeEncoder.EncodeInt64(value), 0, 8, async);
		}

		public ValueTask Write(float value, AsyncWrappingCommonArgs async)
		{
			var buffer = BitConverter.GetBytes(value);
			return Write(BitConverter.ToInt32(buffer, 0), async);
		}

		public ValueTask Write(double value, AsyncWrappingCommonArgs async)
		{
			var buffer = BitConverter.GetBytes(value);
			return Write(BitConverter.ToInt64(buffer, 0), async);
		}

		public ValueTask Write(decimal value, int type, int scale, AsyncWrappingCommonArgs async)
		{
			var numeric = TypeEncoder.EncodeDecimal(value, scale, type);
			switch (type & ~1)
			{
				case IscCodes.SQL_SHORT:
					return Write((short)numeric, async);
				case IscCodes.SQL_LONG:
					return Write((int)numeric, async);
				case IscCodes.SQL_QUAD:
				case IscCodes.SQL_INT64:
					return Write((long)numeric, async);
				case IscCodes.SQL_DOUBLE:
				case IscCodes.SQL_D_FLOAT:
					return Write((double)numeric, async);
				case IscCodes.SQL_INT128:
					return Write((BigInteger)numeric, async);
				default:
					throw new ArgumentOutOfRangeException(nameof(type), $"{nameof(type)}={type}");
			}
		}

		public ValueTask Write(bool value, AsyncWrappingCommonArgs async)
		{
			return WriteOpaque(TypeEncoder.EncodeBoolean(value), async);
		}

		public async ValueTask Write(DateTime value, AsyncWrappingCommonArgs async)
		{
			await WriteDate(value, async).ConfigureAwait(false);
			await WriteTime(TypeHelper.DateTimeToTimeSpan(value), async).ConfigureAwait(false);
		}

		public ValueTask Write(Guid value, AsyncWrappingCommonArgs async)
		{
			return WriteOpaque(TypeEncoder.EncodeGuid(value), async);
		}

		public ValueTask Write(FbDecFloat value, int size, AsyncWrappingCommonArgs async)
		{
			return WriteOpaque(size switch
			{
				16 => TypeEncoder.EncodeDec16(value),
				34 => TypeEncoder.EncodeDec34(value),
				_ => throw new ArgumentOutOfRangeException(),
			}, async);
		}

		public ValueTask Write(BigInteger value, AsyncWrappingCommonArgs async)
		{
			return WriteOpaque(TypeEncoder.EncodeInt128(value), async);
		}

		public ValueTask WriteDate(DateTime value, AsyncWrappingCommonArgs async)
		{
			return Write(TypeEncoder.EncodeDate(Convert.ToDateTime(value)), async);
		}

		public ValueTask WriteTime(TimeSpan value, AsyncWrappingCommonArgs async)
		{
			return Write(TypeEncoder.EncodeTime(value), async);
		}

		#endregion

		#region Pad + Fill

		readonly static byte[] PadArray = new byte[] { 0, 0, 0, 0 };
		ValueTask WritePad(int length, AsyncWrappingCommonArgs async)
		{
			return _dataProvider.Write(PadArray, 0, length, async);
		}

		async ValueTask ReadPad(int length, AsyncWrappingCommonArgs async)
		{
			Debug.Assert(length < _smallBuffer.Length);
			await ReadBytes(_smallBuffer, length, async).ConfigureAwait(false);
		}

		readonly static byte[] FillArray = Enumerable.Repeat((byte)32, 32767).ToArray();
		ValueTask WriteFill(int length, AsyncWrappingCommonArgs async)
		{
			return _dataProvider.Write(FillArray, 0, length, async);
		}

		#endregion
	}
}
