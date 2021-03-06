/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Microsoft Public License. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Microsoft Public License, please send an email to 
 * dlr@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Microsoft Public License.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;

using Microsoft.Scripting;
using Microsoft.Scripting.Math;
using Microsoft.Scripting.Runtime;

using IronPython.Runtime;
using IronPython.Runtime.Operations;
using IronPython.Runtime.Types;

#if !SILVERLIGHT

namespace IronPython.Modules {
    /// <summary>
    /// Provides support for interop with native code from Python code.
    /// </summary>
    public static partial class CTypes {       
        /// <summary>
        /// Meta class for structures.  Validates _fields_ on creation, provides factory
        /// methods for creating instances from addresses and translating to parameters.
        /// </summary>
        [PythonType, PythonHidden]
        public class StructType : PythonType, INativeType {
            internal Field[] _fields;
            private int? _size, _alignment, _pack;

            public StructType(CodeContext/*!*/ context, string name, PythonTuple bases, IAttributesCollection members)
                : base(context, name, bases, members) {

                foreach (PythonType pt in ResolutionOrder) {
                    StructType st = pt as StructType;
                    if (st != this && st != null) {
                        st.EnsureFinal();
                    }

                    UnionType ut = pt as UnionType;
                    if (ut != null) {
                        ut.EnsureFinal();
                    }
                }

                object pack;
                if (members.TryGetValue(SymbolTable.StringToId("_pack_"), out pack)) {
                    if (!(pack is int) || ((int)pack < 0)) {
                        throw PythonOps.ValueError("pack must be a non-negative integer");
                    }
                    _pack = (int)pack;
                }

                object fields;
                if (members.TryGetValue(SymbolTable.StringToId("_fields_"), out fields)) {
                    SetFields(fields);
                }

                // TODO: _anonymous_
            }

            private StructType(Type underlyingSystemType)
                : base(underlyingSystemType) {
            }

            public static ArrayType/*!*/ operator *(StructType type, int count) {
                return MakeArrayType(type, count);
            }

            public static ArrayType/*!*/ operator *(int count, StructType type) {
                return MakeArrayType(type, count);
            }

            /// <summary>
            /// Converts an object into a function call parameter.
            /// 
            /// Structures just return themselves.
            /// </summary>
            public object from_param(object obj) {
                if (!Builtin.isinstance(obj, this)) {
                    throw PythonOps.TypeError("expected {0} instance got {1}", Name, PythonTypeOps.GetName(obj));
                }

                return obj;
            }

            public _Structure from_address(CodeContext/*!*/ context, int address) {
                return from_address(context, new IntPtr(address));
            }

            public _Structure from_address(CodeContext/*!*/ context, BigInteger address) {
                return from_address(context, new IntPtr(address.ToInt64()));
            }

            public _Structure from_address(CodeContext/*!*/ context, IntPtr ptr) {
                _Structure res = (_Structure)CreateInstance(context);
                res.SetAddress(ptr);
                return res;
            }

            public object in_dll(object library, string name) {
                throw new NotImplementedException("in dll");
            }

            public new void __setattr__(CodeContext/*!*/ context, string name, object value) {
                if (name == "_fields_") {
                    lock (this) {
                        if (_fields != null) {
                            throw PythonOps.AttributeError("_fields_ is final");
                        }

                        SetFields(value);
                    }
                }

                base.__setattr__(context, name, value);
            }

            #region INativeType Members

            int INativeType.Size {
                get {
                    EnsureSizeAndAlignment();

                    return _size.Value;
                }
            }

            int INativeType.Alignment {
                get {
                    EnsureSizeAndAlignment();

                    return _alignment.Value;
                }
            }

            object INativeType.GetValue(MemoryHolder/*!*/ owner, int offset, bool raw) {
                _Structure res = (_Structure)CreateInstance(this.Context.SharedContext);
                res._memHolder = owner.GetSubBlock(offset);
                return res;
            }

            void INativeType.SetValue(MemoryHolder/*!*/ address, int offset, object value) {
                try {
                    SetValueInternal(address, offset, value);
                } catch (ArgumentTypeException e) {
                    throw PythonOps.RuntimeError("({0}) <type 'exceptions.TypeError'>: {1}",
                        Name,
                        e.Message);
                } catch (ArgumentException e) {
                    throw PythonOps.RuntimeError("({0}) <type 'exceptions.ValueError'>: {1}",
                        Name,
                        e.Message);
                }
            }

            internal void SetValueInternal(MemoryHolder address, int offset, object value) {
                IList<object> init = value as IList<object>;
                if (init != null) {
                    if (init.Count > _fields.Length) {
                        throw PythonOps.TypeError("too many initializers");
                    }

                    for (int i = 0; i < init.Count; i++) {
                        _fields[i].SetValue(address, offset, init[i]);
                    }
                } else {
                    CData data = value as CData;
                    if (data != null) {
                        data._memHolder.CopyTo(address, offset, data.Size);
                    } else {
                        throw new NotImplementedException("set value");
                    }
                }
            }

            Type/*!*/ INativeType.GetNativeType() {
                EnsureFinal();

                return GetMarshalTypeFromSize(_size.Value);
            }

            MarshalCleanup INativeType.EmitMarshalling(ILGenerator/*!*/ method, LocalOrArg argIndex, List<object>/*!*/ constantPool, int constantPoolArgument) {
                Type argumentType = argIndex.Type;
                argIndex.Emit(method);
                if (argumentType.IsValueType) {
                    method.Emit(OpCodes.Box, argumentType);
                }
                constantPool.Add(this);
                method.Emit(OpCodes.Ldarg, constantPoolArgument);
                method.Emit(OpCodes.Ldc_I4, constantPool.Count - 1);
                method.Emit(OpCodes.Ldelem_Ref);
                method.Emit(OpCodes.Call, typeof(ModuleOps).GetMethod("CheckCDataType"));
                method.Emit(OpCodes.Call, typeof(CData).GetMethod("get_UnsafeAddress"));
                method.Emit(OpCodes.Ldobj, ((INativeType)this).GetNativeType());
                return null;
            }

            Type/*!*/ INativeType.GetPythonType() {
                return typeof(object);
            }

            void INativeType.EmitReverseMarshalling(ILGenerator method, LocalOrArg value, List<object> constantPool, int constantPoolArgument) {
                value.Emit(method);
                EmitCDataCreation(this, method, constantPool, constantPoolArgument);
            }

            #endregion

            internal static PythonType MakeSystemType(Type underlyingSystemType) {
                return PythonType.SetPythonType(underlyingSystemType, new StructType(underlyingSystemType));
            }

            private void SetFields(object fields) {
                lock (this) {
                    IList<object> list = GetFieldsList(fields);

                    int size;
                    int alignment;
                    int? bitCount = null;
                    int? curBitCount = null;
                    INativeType lastType = null;
                    List<Field> allFields = GetBaseSizeAlignmentAndFields(out size, out alignment);

                    for (int fieldIndex = 0; fieldIndex < list.Count; fieldIndex++) {
                        object o = list[fieldIndex];
                        string fieldName;
                        INativeType cdata;
                        GetFieldInfo(this, o, out fieldName, out cdata, out bitCount);

                        int prevSize = UpdateSizeAndAlignment(cdata, bitCount, lastType, ref size, ref alignment, ref curBitCount);

                        Field newField = new Field(cdata, prevSize, allFields.Count, bitCount, curBitCount - bitCount);
                        allFields.Add(newField);
                        AddSlot(SymbolTable.StringToId(fieldName), newField);

                        lastType = cdata;
                    }

                    if (bitCount != null) {
                        size += lastType.Size;
                    }

                    _fields = allFields.ToArray();
                    _size = PythonStruct.Align(size, alignment);
                    _alignment = alignment;
                }
            }

            private List<Field> GetBaseSizeAlignmentAndFields(out int size, out int alignment) {
                size = 0;
                alignment = 1;
                List<Field> allFields = new List<Field>();
                INativeType lastType = null;
                int? totalBitCount = null;
                foreach (PythonType pt in BaseTypes) {
                    StructType st = pt as StructType;
                    if (st != null) {
                        foreach (Field f in st._fields) {
                            allFields.Add(f);
                            UpdateSizeAndAlignment(f.NativeType, f.BitCount, lastType, ref size, ref alignment, ref totalBitCount);

                            if (f.NativeType == this) {
                                throw StructureCannotContainSelf();
                            }

                            lastType = f.NativeType;
                        }
                    }
                }
                return allFields;
            }

            private int UpdateSizeAndAlignment(INativeType cdata, int? bitCount, INativeType lastType, ref int size, ref int alignment, ref int? totalBitCount) {
                int prevSize = size;
                if (bitCount != null) {
                    if (lastType != null && lastType.Size != cdata.Size) {
                        totalBitCount = null;
                        prevSize = size += lastType.Size;
                    }

                    size = PythonStruct.Align(size, cdata.Alignment);

                    if (totalBitCount != null) {
                        if ((bitCount + totalBitCount + 7) / 8 <= cdata.Size) {
                            totalBitCount = bitCount + totalBitCount;
                        } else {
                            size += lastType.Size;
                            prevSize = size;
                            totalBitCount = bitCount;
                        }
                    } else {
                        totalBitCount = bitCount;
                    }
                } else {
                    if (totalBitCount != null) {
                        size += lastType.Size;
                        prevSize = size;
                        totalBitCount = null;
                    }

                    if (_pack != null) {
                        alignment = _pack.Value;
                        prevSize = size = PythonStruct.Align(size, _pack.Value);

                        size += cdata.Size;
                    } else {
                        alignment = Math.Max(alignment, cdata.Alignment);
                        prevSize = size = PythonStruct.Align(size, cdata.Alignment);
                        size += cdata.Size;
                    }
                }

                return prevSize;
            }

            internal void EnsureFinal() {
                if (_fields == null) {
                    SetFields(PythonTuple.EMPTY);
                }
            }

            /// <summary>
            /// If our size/alignment hasn't been initialized then grabs the size/alignment
            /// from all of our base classes.  If later new _fields_ are added we'll be
            /// initialized and these values will be replaced.
            /// </summary>
            private void EnsureSizeAndAlignment() {
                Debug.Assert(_size.HasValue == _alignment.HasValue);
                // these are always iniitalized together
                if (_size == null) {
                    lock (this) {
                        if (_size == null) {
                            int size, alignment;
                            GetBaseSizeAlignmentAndFields(out size, out alignment);
                            _size = size;
                            _alignment = alignment;
                        }
                    }
                }
            }
        }
    }
}

#endif
