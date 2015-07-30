﻿#region LICENSE
// ---------------------------------- LICENSE ---------------------------------- //
//
//    Fling OS - The educational operating system
//    Copyright (C) 2015 Edward Nutting
//
//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 2 of the License, or
//    (at your option) any later version.
//
//    This program is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//    GNU General Public License for more details.
//
//    You should have received a copy of the GNU General Public License
//    along with this program.  If not, see <http://www.gnu.org/licenses/>.
//
//  Project owner: 
//		Email: edwardnutting@outlook.com
//		For paper mail address, please contact via email for details.
//
// ------------------------------------------------------------------------------ //
#endregion
    
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Drivers.Compiler.IL
{
    /// <summary>
    /// The IL Sanner manages scanning types, fields and methods to generate the final assembly code.
    /// </summary>
    /// <remarks>
    /// Some parts of this (e.g. GetAllocStringForSize) generate ASM but they shouldn't. The code
    /// generating ASM should be shifted to the target architecture library.
    /// </remarks>
    public static class ILScanner
    {
        /// <summary>
        /// The target architecture library.
        /// </summary>
        /// <remarks>
        /// Used for loading IL and ASM ops used to convert IL to ASM and ASM to machine code
        /// for the target architecture.
        /// </remarks>
        private static System.Reflection.Assembly TargetArchitectureAssembly = null;
        /// <summary>
        /// Map of op codes to IL ops which are loaded from the target architecture.
        /// </summary>
        public static Dictionary<ILOp.OpCodes, ILOp> TargetILOps = new Dictionary<ILOp.OpCodes, ILOp>();
        /// <summary>
        /// The method start IL op. This is a fake IL op used by the Drivers Compiler.
        /// </summary>
        public static ILOps.MethodStart MethodStartOp;
        /// <summary>
        /// The method end IL op. This is a fake IL op used by the Drivers Compiler.
        /// </summary>
        public static ILOps.MethodEnd MethodEndOp;
        /// <summary>
        /// The stack switch IL op. This is a fake IL op used by the Drivers Compiler.
        /// </summary>
        public static ILOps.StackSwitch StackSwitchOp;

        public static Dictionary<ASM.OpCodes, Type> TargetASMOps = new Dictionary<ASM.OpCodes, Type>();

        /// <summary>
        /// Initialises the IL scanner.
        /// </summary>
        /// <remarks>
        /// Loads the target architecture library.
        /// </remarks>
        /// <returns>True if initialisation was successful. Otherwise, false.</returns>
        public static bool Init()
        {
            bool OK = true;

            OK = LoadTargetArchiecture();

            return OK;
        }
        /// <summary>
        /// Loads the target architecture library and fills in the TargetILOps, MethodStartOp, MethodEndOp and StackSwitchOp
        /// fields.
        /// </summary>
        /// <returns>True if fully loaded without error. Otherwise, false.</returns>
        private static bool LoadTargetArchiecture()
        {
            bool OK = false;

            try
            {
                switch (Options.TargetArchitecture)
                {
                    case "x86":
                        {
                            string dir = System.IO.Path.GetDirectoryName(typeof(ILCompiler).Assembly.Location);
                            string fileName = System.IO.Path.Combine(dir, @"Drivers.Compiler.Architectures.x86.dll");
                            fileName = System.IO.Path.GetFullPath(fileName);
                            TargetArchitectureAssembly = System.Reflection.Assembly.LoadFrom(fileName);
                            OK = true;
                        }
                        break;
                    default:
                        OK = false;
                        throw new ArgumentException("Unrecognised target architecture!");
                }

                if (OK)
                {
                    Type[] AllTypes = TargetArchitectureAssembly.GetTypes();
                    foreach (Type aType in AllTypes)
                    {
                        if (aType.IsSubclassOf(typeof(ILOp)))
                        {
                            if (aType.IsSubclassOf(typeof(ILOps.MethodStart)))
                            {
                                MethodStartOp = (ILOps.MethodStart)aType.GetConstructor(new Type[0]).Invoke(new object[0]);
                            }
                            else if (aType.IsSubclassOf(typeof(ILOps.MethodEnd)))
                            {
                                MethodEndOp = (ILOps.MethodEnd)aType.GetConstructor(new Type[0]).Invoke(new object[0]);
                            }
                            else if (aType.IsSubclassOf(typeof(ILOps.StackSwitch)))
                            {
                                StackSwitchOp = (ILOps.StackSwitch)aType.GetConstructor(new Type[0]).Invoke(new object[0]);
                            }
                            else
                            {
                                ILOps.ILOpTargetAttribute[] targetAttrs = (ILOps.ILOpTargetAttribute[])aType.GetCustomAttributes(typeof(ILOps.ILOpTargetAttribute), true);
                                if (targetAttrs == null || targetAttrs.Length == 0)
                                {
                                    throw new Exception("ILScanner could not load target architecture ILOp because target attribute was not specified!");
                                }
                                else
                                {
                                    foreach (ILOps.ILOpTargetAttribute targetAttr in targetAttrs)
                                    {
                                        TargetILOps.Add(targetAttr.Target, (ILOp)aType.GetConstructor(new Type[0]).Invoke(new object[0]));
                                    }
                                }
                            }
                        }
                        else if (aType.IsSubclassOf(typeof(ASM.ASMOp)))
                        {
                            ASM.ASMOpTargetAttribute[] targetAttrs = (ASM.ASMOpTargetAttribute[])aType.GetCustomAttributes(typeof(ASM.ASMOpTargetAttribute), true);
                            foreach (ASM.ASMOpTargetAttribute targetAttr in targetAttrs)
                            {
                                TargetASMOps.Add(targetAttr.Target, aType);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OK = false;
                Logger.LogError(Errors.ILCompiler_LoadTargetArchError_ErrorCode, "", 0, 
                    string.Format(Errors.ErrorMessages[Errors.ILCompiler_LoadTargetArchError_ErrorCode],
                                    ex.Message));
            }

            return OK;
        }

        /// <summary>
        /// Map of type IDs to the library from which they originated. 
        /// </summary>
        /// <remarks>
        /// Used to detect when types are external to the library being compiled.
        /// </remarks>
        private static Dictionary<string, ILLibrary> ScannedTypes = new Dictionary<string, ILLibrary>();
        /// <summary>
        /// Scans the specified library and any dependencies.
        /// </summary>
        /// <param name="TheLibrary">The library to scan.</param>
        /// <returns>
        /// CompileResult.OK if completed successfully. 
        /// Otherwise CompileResult.PartialFail or CompileResult.Error depending on 
        /// the extent of the problem.
        /// </returns>
        public static CompileResult Scan(ILLibrary TheLibrary)
        {
            CompileResult result = CompileResult.OK;

            if (TheLibrary.ILScanned)
            {
                return result;
            }
            TheLibrary.ILScanned = true;

            foreach (ILLibrary depLib in TheLibrary.Dependencies)
            {
                Scan(depLib);
            }
            
            // Create / Add Static Fields ASM Block
            ASM.ASMBlock StaticFieldsBlock = new ASM.ASMBlock()
            {
                Priority = (long.MinValue / 2) - 9
            };
            TheLibrary.TheASMLibrary.ASMBlocks.Add(StaticFieldsBlock);

            // Create / Add Types Table ASM Block
            ASM.ASMBlock TypesTableBlock = new ASM.ASMBlock()
            {
                Priority = (long.MinValue / 2) - 8
            };
            TheLibrary.TheASMLibrary.ASMBlocks.Add(TypesTableBlock);

            // Create / Add Method Tables ASM Block
            ASM.ASMBlock MethodTablesBlock = new ASM.ASMBlock()
            {
                Priority = (long.MinValue / 2) + 0
            };
            TheLibrary.TheASMLibrary.ASMBlocks.Add(MethodTablesBlock);

            // Create / Add Field Tables ASM Block
            ASM.ASMBlock FieldTablesBlock = new ASM.ASMBlock()
            {
                Priority = (long.MinValue / 2) + 1
            };
            TheLibrary.TheASMLibrary.ASMBlocks.Add(FieldTablesBlock);

            // Don't use foreach or you get collection modified exceptions
            for (int i = 0; i < TheLibrary.TypeInfos.Count; i++)
            {
                Types.TypeInfo aTypeInfo = TheLibrary.TypeInfos[i];
                if (!ScannedTypes.ContainsKey(aTypeInfo.ID))
                {
                    ScannedTypes.Add(aTypeInfo.ID, TheLibrary);
                    ScanStaticFields(TheLibrary, aTypeInfo, StaticFieldsBlock);
                    ScanType(TheLibrary, aTypeInfo, TypesTableBlock);
                    ScanMethods(TheLibrary, aTypeInfo, MethodTablesBlock);
                    ScanFields(TheLibrary, aTypeInfo, FieldTablesBlock);
                }
            }

            foreach (Types.MethodInfo aMethodInfo in TheLibrary.ILBlocks.Keys)
            {
                ILBlock anILBlock = TheLibrary.ILBlocks[aMethodInfo];
                CompileResult singleResult = CompileResult.OK;

                if (anILBlock.Plugged)
                {
                    singleResult = ScanPluggedILBlock(TheLibrary, aMethodInfo, anILBlock);
                }
                else
                {
                    singleResult = ScanNonpluggedILBlock(TheLibrary, aMethodInfo, anILBlock);
                }
            
                if (result != CompileResult.OK)
                {
                    result = singleResult;
                }
            }

            // Create / Add String Literals ASM Block
            #region String Literals Block

            ASM.ASMBlock StringLiteralsBlock = new ASM.ASMBlock()
            {
                Priority = (long.MinValue / 2) - 10
            };
            TheLibrary.TheASMLibrary.ASMBlocks.Add(StringLiteralsBlock);

            string StringTypeId = ILLibrary.SpecialClasses[typeof(Attributes.StringClassAttribute)].First().ID;
            StringLiteralsBlock.AddExternalLabel(StringTypeId);
            foreach (KeyValuePair<string, string> aStringLiteral in TheLibrary.StringLiterals)
            {
                string value = aStringLiteral.Value;
                byte[] lengthBytes = BitConverter.GetBytes(value.Length);

                ASM.ASMOp newLiteralOp = (ASM.ASMOp)Activator.CreateInstance(TargetASMOps[ASM.OpCodes.StringLiteral], aStringLiteral.Key, StringTypeId, lengthBytes, value.ToCharArray());
                StringLiteralsBlock.Append(newLiteralOp);
            }

            #endregion

            return result;
        }

        /// <summary>
        /// The number of types scanned.
        /// </summary>
        /// <remarks>
        /// Used as an ID generator for the types table(s).
        /// </remarks>
        private static int TypesScanned = 1;
        /// <summary>
        /// Scans the specified type (excludes fields and methods).
        /// </summary>
        /// <param name="TheLibrary">The library currently being compiled.</param>
        /// <param name="TheTypeInfo">The type to scan.</param>
        /// <param name="TypesTableBlock">The ASM block for the types table for the library currently being compiled.</param>
        private static void ScanType(ILLibrary TheLibrary, Types.TypeInfo TheTypeInfo, ASM.ASMBlock TypesTableBlock)
        {
            string TypeId = TheTypeInfo.ID;
            string SizeVal = TheTypeInfo.SizeOnHeapInBytes.ToString();
            string IdVal = (TypesScanned++).ToString();
            string StackSizeVal = TheTypeInfo.SizeOnStackInBytes.ToString();
            string IsValueTypeVal = (TheTypeInfo.IsValueType ? "1" : "0");
            string MethodTablePointer = TypeId + "_MethodTable";
            string IsPointerTypeVal = (TheTypeInfo.IsPointer ? "1" : "0");
            string BaseTypeIdVal = "0";
            if (TheTypeInfo.UnderlyingType.BaseType != null)
            {
                if (!TheTypeInfo.UnderlyingType.BaseType.AssemblyQualifiedName.Contains("mscorlib"))
                {
                    Types.TypeInfo baseTypeInfo = TheLibrary.GetTypeInfo(TheTypeInfo.UnderlyingType.BaseType);
                    BaseTypeIdVal = baseTypeInfo.ID;
                    //Declared external to this library, so won't appear in this library's type tables
                    if ((ScannedTypes.ContainsKey(baseTypeInfo.ID) &&
                         ScannedTypes[baseTypeInfo.ID] != TheLibrary) ||
                        !TheLibrary.TypeInfos.Contains(baseTypeInfo))
                    {
                        TypesTableBlock.AddExternalLabel(BaseTypeIdVal);
                    }
                }
            }
            string FieldTablePointer = TypeId + "_FieldTable";
            string TypeSignatureLiteralLabel = TheLibrary.AddStringLiteral(TheTypeInfo.UnderlyingType.FullName); // Legacy
            string TypeIdLiteralLabel = TheLibrary.AddStringLiteral(TheTypeInfo.ID);

            Types.TypeInfo typeTypeInfo = ILLibrary.SpecialClasses[typeof(Attributes.TypeClassAttribute)].First();
            List<Types.FieldInfo> OrderedFields = typeTypeInfo.FieldInfos.Where(x => !x.IsStatic).OrderBy(x => x.OffsetInBytes).ToList();
            List<Tuple<string, Types.TypeInfo>> FieldInformation = new List<Tuple<string, Types.TypeInfo>>();
            foreach (Types.FieldInfo aTypeField in OrderedFields)
            {
                Types.TypeInfo FieldTypeInfo = TheLibrary.GetTypeInfo(aTypeField.FieldType);
                FieldInformation.Add(new Tuple<string, Types.TypeInfo>(aTypeField.Name, FieldTypeInfo));
            }

            ASM.ASMOp newTypeTableOp = (ASM.ASMOp)Activator.CreateInstance(TargetASMOps[ASM.OpCodes.TypeTable], TypeId, SizeVal, IdVal, StackSizeVal, IsValueTypeVal, MethodTablePointer, IsPointerTypeVal, BaseTypeIdVal, FieldTablePointer, TypeSignatureLiteralLabel, TypeIdLiteralLabel, FieldInformation);
            TypesTableBlock.Append(newTypeTableOp);

            TypesTableBlock.AddExternalLabel(MethodTablePointer);
            TypesTableBlock.AddExternalLabel(FieldTablePointer);
            TypesTableBlock.AddExternalLabel(TypeSignatureLiteralLabel);
            TypesTableBlock.AddExternalLabel(TypeIdLiteralLabel);
        }
        /// <summary>
        /// Scans the specified type's static fields.
        /// </summary>
        /// <param name="TheLibrary">The library currently being compiled.</param>
        /// <param name="TheTypeInfo">The type to scan the static fields of.</param>
        /// <param name="StaticFieldsBlock">The ASM block for the static fields for the library currently being compiled.</param>
        private static void ScanStaticFields(ILLibrary TheLibrary, Types.TypeInfo TheTypeInfo, ASM.ASMBlock StaticFieldsBlock)
        {
            foreach (Types.FieldInfo aFieldInfo in TheTypeInfo.FieldInfos)
            {
                if (aFieldInfo.IsStatic)
                {
                    string FieldID = aFieldInfo.ID;
                    Types.TypeInfo fieldTypeInfo = TheLibrary.GetTypeInfo(aFieldInfo.FieldType);
                    int Size = /*fieldTypeInfo.IsValueType ? fieldTypeInfo.SizeOnHeapInBytes : */fieldTypeInfo.SizeOnStackInBytes;
                    StaticFieldsBlock.Append(new ASM.ASMGeneric() {
                        Text = string.Format("GLOBAL {0}:data\r\n{0}: times {1} db 0", FieldID, Size)
                    });
                }
            }
        }
        /// <summary>
        /// Scans the specified type's methods.
        /// </summary>
        /// <param name="TheLibrary">The library currently being compiled.</param>
        /// <param name="TheTypeInfo">The type to scan the methods of.</param>
        /// <param name="MethodTablesBlock">The ASM block for the methods table for the library currently being compiled.</param>
        private static void ScanMethods(ILLibrary TheLibrary, Types.TypeInfo TheTypeInfo, ASM.ASMBlock MethodTablesBlock)
        {
            string currentTypeId = TheTypeInfo.ID;
            string currentTypeName = TheTypeInfo.UnderlyingType.FullName;

            List<Tuple<string, string>> AllMethodInfo = new List<Tuple<string, string>>();
            
            if (TheTypeInfo.UnderlyingType.BaseType == null || TheTypeInfo.UnderlyingType.BaseType.FullName != "System.Array")
            {
                foreach (Types.MethodInfo aMethodInfo in TheTypeInfo.MethodInfos)
                {
                    if (!aMethodInfo.IsStatic && !aMethodInfo.UnderlyingInfo.IsAbstract)
                    {
                        string methodID = aMethodInfo.ID;
                        string methodIDValue = aMethodInfo.IDValue.ToString();

                        MethodTablesBlock.AddExternalLabel(methodID);

                        AllMethodInfo.Add(new Tuple<string, string>(methodID, methodIDValue));
                    }
                }
            }

            Types.TypeInfo InformationAboutMethodInfoStruct = ILLibrary.SpecialClasses[typeof(Attributes.MethodInfoStructAttribute)].First();
            List<Types.FieldInfo> MethodInfoStruct_OrderedFields = InformationAboutMethodInfoStruct.FieldInfos.Where(x => !x.IsStatic).OrderBy(x => x.OffsetInBytes).ToList();
            List<Tuple<string, int>> MethodInfoStruct_OrderedFieldInfo_Subset = new List<Tuple<string, int>>();
            foreach (Types.FieldInfo aField in MethodInfoStruct_OrderedFields)
            {
                Types.TypeInfo FieldTypeInfo = TheLibrary.GetTypeInfo(aField.FieldType);
                MethodInfoStruct_OrderedFieldInfo_Subset.Add(new Tuple<string, int>(aField.Name,
                    FieldTypeInfo.IsValueType ? FieldTypeInfo.SizeOnHeapInBytes : FieldTypeInfo.SizeOnStackInBytes));
            }

            string parentTypeMethodTablePtr = "0";
            bool parentPtrIsExternal = false;
            if (TheTypeInfo.UnderlyingType.BaseType != null)
            {
                if (!TheTypeInfo.UnderlyingType.BaseType.AssemblyQualifiedName.Contains("mscorlib"))
                {
                    Types.TypeInfo baseTypeInfo = TheLibrary.GetTypeInfo(TheTypeInfo.UnderlyingType.BaseType);
                    parentPtrIsExternal = (ScannedTypes.ContainsKey(baseTypeInfo.ID) && ScannedTypes[baseTypeInfo.ID] != TheLibrary) 
                        || !TheLibrary.TypeInfos.Contains(baseTypeInfo);
                    parentTypeMethodTablePtr = baseTypeInfo.ID + "_MethodTable";
                }
            }
            {
                string methodID = parentTypeMethodTablePtr;
                string methodIDValue = "0";

                if (parentPtrIsExternal)
                {
                    MethodTablesBlock.AddExternalLabel(methodID);
                }

                AllMethodInfo.Add(new Tuple<string,string>(methodID, methodIDValue));
            }

            ASM.ASMOp newMethodTableOp = (ASM.ASMOp)Activator.CreateInstance(TargetASMOps[ASM.OpCodes.MethodTable], currentTypeId, currentTypeName, AllMethodInfo, MethodInfoStruct_OrderedFieldInfo_Subset);
            MethodTablesBlock.Append(newMethodTableOp);
        }
        /// <summary>
        /// Scans the specified type's non-static fields.
        /// </summary>
        /// <param name="TheLibrary">The library currently being compiled.</param>
        /// <param name="TheTypeInfo">The type to scan the non-static fields of.</param>
        /// <param name="FieldTablesBlock">The ASM block for the fields table for the library currently being compiled.</param>
        private static void ScanFields(ILLibrary TheLibrary, Types.TypeInfo TheTypeInfo, ASM.ASMBlock FieldTablesBlock)
        {
            string currentTypeId = TheTypeInfo.ID;
            string currentTypeName = TheTypeInfo.UnderlyingType.FullName;
            List<Tuple<string, string, string>> AllFieldInfo = new List<Tuple<string, string, string>>();

            if (TheTypeInfo.UnderlyingType.BaseType == null || (TheTypeInfo.UnderlyingType.BaseType.FullName != "System.Array" &&
                                                                TheTypeInfo.UnderlyingType.BaseType.FullName != "System.MulticastDelegate"))
            {
                foreach (Types.FieldInfo anOwnField in TheTypeInfo.FieldInfos)
                {
                    if (!anOwnField.IsStatic)
                    {
                        Types.TypeInfo FieldTypeInfo = TheLibrary.GetTypeInfo(anOwnField.FieldType);

                        string fieldOffsetVal = anOwnField.OffsetInBytes.ToString();
                        string fieldSizeVal = (FieldTypeInfo.IsValueType ? FieldTypeInfo.SizeOnHeapInBytes : FieldTypeInfo.SizeOnStackInBytes).ToString();
                        string fieldTypeIdVal = FieldTypeInfo.ID;

                        FieldTablesBlock.AddExternalLabel(fieldTypeIdVal);
                        AllFieldInfo.Add(new Tuple<string, string, string>(fieldOffsetVal, fieldSizeVal, fieldTypeIdVal));
                    }
                }
            }

            Types.TypeInfo InformationAboutFieldInfoStruct = ILLibrary.SpecialClasses[typeof(Attributes.FieldInfoStructAttribute)].First();
            List<Types.FieldInfo> FieldInfoStruct_OrderedFields = InformationAboutFieldInfoStruct.FieldInfos.Where(x => !x.IsStatic).OrderBy(x => x.OffsetInBytes).ToList();
            List<Tuple<string, int>> FieldInfoStruct_OrderedFieldInfo_Subset = new List<Tuple<string, int>>();
            foreach (Types.FieldInfo aField in FieldInfoStruct_OrderedFields)
            {
                Types.TypeInfo FieldTypeInfo = TheLibrary.GetTypeInfo(aField.FieldType);
                FieldInfoStruct_OrderedFieldInfo_Subset.Add(new Tuple<string, int>(aField.Name,
                    FieldTypeInfo.IsValueType ? FieldTypeInfo.SizeOnHeapInBytes : FieldTypeInfo.SizeOnStackInBytes));
            }

            string parentTypeFieldTablePtr = "0";
            bool parentPtrIsExternal = false;
            if (TheTypeInfo.UnderlyingType.BaseType != null)
            {
                if (!TheTypeInfo.UnderlyingType.BaseType.AssemblyQualifiedName.Contains("mscorlib"))
                {
                    Types.TypeInfo baseTypeInfo = TheLibrary.GetTypeInfo(TheTypeInfo.UnderlyingType.BaseType);
                    parentPtrIsExternal = (ScannedTypes.ContainsKey(baseTypeInfo.ID) &&
                         ScannedTypes[baseTypeInfo.ID] != TheLibrary) || !TheLibrary.TypeInfos.Contains(baseTypeInfo);
                    parentTypeFieldTablePtr = baseTypeInfo.ID + "_FieldTable";
                }
            }
            {
                string fieldOffsetVal = "0";
                string fieldSizeVal = "0";
                string fieldTypeIdVal = parentTypeFieldTablePtr;

                if (parentPtrIsExternal)
                {
                    FieldTablesBlock.AddExternalLabel(fieldTypeIdVal);
                }

                AllFieldInfo.Add(new Tuple<string, string, string>(fieldOffsetVal, fieldSizeVal, fieldTypeIdVal));
            }

            ASM.ASMOp newFieldTableOp = (ASM.ASMOp)Activator.CreateInstance(TargetASMOps[ASM.OpCodes.FieldTable], currentTypeId, currentTypeName, AllFieldInfo, FieldInfoStruct_OrderedFieldInfo_Subset);
            FieldTablesBlock.Append(newFieldTableOp);
        }

        public static string GetAllocStringForSize(int numBytes)
        {
            switch (numBytes)
            {
                case 1:
                    return "db";
                case 2:
                    return "dw";
                case 4:
                    return "dd";
                default:
                    return "NOSIZEALLOC";
            }
        }

        /// <summary>
        /// Scans the specified plugged IL block.
        /// </summary>
        /// <param name="TheLibrary">The library currently being compiled.</param>
        /// <param name="theMethodInfo">The method which generated the IL block.</param>
        /// <param name="theILBlock">The IL block to scan.</param>
        /// <returns>CompileResult.OK.</returns>
        private static CompileResult ScanPluggedILBlock(ILLibrary TheLibrary, Types.MethodInfo theMethodInfo, ILBlock theILBlock)
        {
            TheLibrary.TheASMLibrary.ASMBlocks.Add(new ASM.ASMBlock()
            {
                PlugPath = theILBlock.PlugPath,
                OriginMethodInfo = theMethodInfo,
                Priority = theMethodInfo.Priority
            });

            return CompileResult.OK;
        }
        /// <summary>
        /// Scans the specified non-plugged IL block.
        /// </summary>
        /// <param name="TheLibrary">The library currently being compiled.</param>
        /// <param name="theMethodInfo">The method which generated the IL block.</param>
        /// <param name="theILBlock">The IL block to scan.</param>
        /// <returns>CompileResult.OK.</returns>
        private static CompileResult ScanNonpluggedILBlock(ILLibrary TheLibrary, Types.MethodInfo theMethodInfo, ILBlock theILBlock)
        {
            CompileResult result = CompileResult.OK;

            ASM.ASMBlock TheASMBlock = new ASM.ASMBlock()
            {
                OriginMethodInfo = theMethodInfo,
                Priority = theMethodInfo.Priority
            };
            
            ILConversionState convState = new ILConversionState()
            {
                TheILLibrary = TheLibrary,
                CurrentStackFrame = new StackFrame(),
                Input = theILBlock,
                Result = TheASMBlock
            };
            foreach (ILOp anOp in theILBlock.ILOps)
            {
                try
                {
                    string commentText = TheASMBlock.GenerateILOpLabel(convState.PositionOf(anOp), "") + "  --  " + anOp.opCode.ToString() + " -- Offset: " + anOp.Offset.ToString("X2");
                    
                    ASM.ASMOp newCommentOp = (ASM.ASMOp)Activator.CreateInstance(TargetASMOps[ASM.OpCodes.Comment], commentText);
                    TheASMBlock.ASMOps.Add(newCommentOp);
                    
                    int currCount = TheASMBlock.ASMOps.Count;
                    if (anOp is ILOps.MethodStart)
                    {
                        MethodStartOp.Convert(convState, anOp);
                    }
                    else if (anOp is ILOps.MethodEnd)
                    {
                        MethodEndOp.Convert(convState, anOp);
                    }
                    else if (anOp is ILOps.StackSwitch)
                    {
                        StackSwitchOp.Convert(convState, anOp);
                    }
                    else
                    {
                        ILOp ConverterOp = TargetILOps[(ILOp.OpCodes)anOp.opCode.Value];
                        ConverterOp.Convert(convState, anOp);
                    }

                    if (anOp.LabelRequired)
                    {
                        if (currCount < TheASMBlock.ASMOps.Count)
                        {
                            TheASMBlock.ASMOps[currCount].ILLabelPosition = convState.PositionOf(anOp);
                            TheASMBlock.ASMOps[currCount].RequiresILLabel = true;
                        }
                    }
                }
                catch (KeyNotFoundException)
                {
                    result = CompileResult.PartialFailure;

                    Logger.LogError(Errors.ILCompiler_ScanILOpFailure_ErrorCode, theMethodInfo.ToString(), anOp.Offset,
                        string.Format(Errors.ErrorMessages[Errors.ILCompiler_ScanILOpFailure_ErrorCode], "Conversion IL op not found: " + Enum.GetName(typeof(ILOp.OpCodes), anOp.opCode.Value) + "."));
                }
                catch (InvalidOperationException)
                {
                    result = CompileResult.PartialFailure;

                    Logger.LogError(Errors.ILCompiler_ScanILOpFailure_ErrorCode, theMethodInfo.ToString(), anOp.Offset,
                        string.Format(Errors.ErrorMessages[Errors.ILCompiler_ScanILOpFailure_ErrorCode], Enum.GetName(typeof(ILOp.OpCodes), anOp.opCode.Value)));
                }
                catch (NotSupportedException ex)
                {
                    result = CompileResult.PartialFailure;

                    Logger.LogError(Errors.ILCompiler_ScanILOpFailure_ErrorCode, theMethodInfo.ToString(), anOp.Offset,
                        string.Format(Errors.ErrorMessages[Errors.ILCompiler_ScanILOpFailure_ErrorCode], "An IL op reported something as not supported. " + Enum.GetName(typeof(ILOp.OpCodes), anOp.opCode.Value) + ". " + ex.Message));
                }
                catch (Exception ex)
                {
                    result = CompileResult.Fail;

                    Logger.LogError(Errors.ILCompiler_ScanILOpFailure_ErrorCode, theMethodInfo.ToString(), anOp.Offset,
                        string.Format(Errors.ErrorMessages[Errors.ILCompiler_ScanILOpFailure_ErrorCode], ex.Message));
                }
            }

            TheLibrary.TheASMLibrary.ASMBlocks.Add(TheASMBlock);

            return result;
        }
    }
}
