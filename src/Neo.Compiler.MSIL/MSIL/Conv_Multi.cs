using Neo.SmartContract;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Neo.Compiler.MSIL
{
    /// <summary>
    /// Convert IL to NeoVM opcode
    /// </summary>
    public partial class ModuleConverter
    {
        private void _ConvertStLoc(ILMethod method, OpCode src, NeoMethod to, int pos)
        {
            if (pos < 7)
            {
                _Convert1by1(VM.OpCode.STLOC0 + (byte)pos, src, to);
            }
            else
            {
                _Convert1by1(VM.OpCode.STLOC, src, to, new byte[] { (byte)pos });
            }
        }

        private void _ConvertLdLoc(ILMethod method, OpCode src, NeoMethod to, int pos)
        {

            if (pos < 7)
            {
                _Convert1by1(VM.OpCode.LDLOC0 + (byte)pos, src, to);
            }
            else
            {
                _Convert1by1(VM.OpCode.LDLOC, src, to, new byte[] { (byte)pos });
            }
        }

        private void _ConvertLdLocA(ILMethod method, OpCode src, NeoMethod to, int pos)
        {
            // There are two cases, and we need to figure out what the reference address is for
            var n1 = method.body_Codes[method.GetNextCodeAddr(src.addr)];
            var n2 = method.body_Codes[method.GetNextCodeAddr(n1.addr)];

            if (n1.code == CodeEx.Initobj)// Initializes the structure and must give the reference address
            {
                //some initobj, need setloc after initobj.save slot first.
                ldloca_slot = pos;
            }
            else if (n2.code == CodeEx.Call && n2.tokenMethod.Is_ctor())
            {
                //some ctor,need  setloc after ctor.save slot first.
                ldloca_slot = pos;
            }
            else
            {
                _ConvertLdLoc(method, src, to, pos);
            }
        }

        private void _ConvertCastclass(ILMethod method, OpCode src, NeoMethod to)
        {
            var type = src.tokenUnknown as Mono.Cecil.TypeReference;
            try
            {
                var dtype = type.Resolve();
                if (dtype.BaseType.FullName == "System.MulticastDelegate" || dtype.BaseType.FullName == "System.Delegate")
                {
                    foreach (var m in dtype.Methods)
                    {
                        if (m.Name == "Invoke")
                        {
                            to.lastparam = m.Parameters.Count;
                            break;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private void _ConvertLdArg(ILMethod method, OpCode src, NeoMethod to, int pos)
        {
            try
            {
                var ptype = method.method.Parameters[pos].ParameterType.Resolve();
                if (ptype.BaseType.FullName == "System.MulticastDelegate" || ptype.BaseType.FullName == "System.Delegate")
                {
                    foreach (var m in ptype.Methods)
                    {
                        if (m.Name == "Invoke")
                        {
                            to.lastparam = m.Parameters.Count;
                            break;
                        }
                    }
                }
            }
            catch
            {
            }

            if (pos < 7)
            {
                _Convert1by1(VM.OpCode.LDARG0 + (byte)pos, src, to);
            }
            else
            {
                _Convert1by1(VM.OpCode.LDARG, src, to, new byte[] { (byte)pos });
            }
        }

        private void _ConvertStArg(OpCode src, NeoMethod to, int pos)
        {
            if (pos < 7)
            {
                _Convert1by1(VM.OpCode.STLOC0 + (byte)pos, src, to);
            }
            else
            {
                _Convert1by1(VM.OpCode.STLOC, src, to, new byte[] { (byte)pos });
            }
        }

        /*
                public bool IsSysCall(Mono.Cecil.MethodDefinition defs, out string name)
                {
                    if (defs == null)
                    {
                        name = "";
                        return false;
                    }
                    foreach (var attr in defs.CustomAttributes)
                    {
                        if (attr.AttributeType.Name == "SyscallAttribute")
                        {
                            var type = attr.ConstructorArguments[0].Type;
                            var value = (string)attr.ConstructorArguments[0].Value;

                            //dosth
                            name = value;
                            return true;
                        }
                        //if(attr.t)
                    }
                    name = "";
                    return false;
                }
        */

        public bool IsAppCall(Mono.Cecil.MethodDefinition defs, out byte[] hash)
        {
            if (defs == null)
            {
                hash = null;
                return false;
            }

            foreach (var attr in defs.CustomAttributes)
            {
                if (attr.AttributeType.Name == "AppcallAttribute")
                {
                    var type = attr.ConstructorArguments[0].Type;
                    var a = attr.ConstructorArguments[0];
                    if (a.Type.FullName == "System.String")
                    {
                        string hashstr = (string)a.Value;

                        try
                        {
                            hash = hashstr.HexString2Bytes();
                            if (hash.Length != 20) throw new Exception("Wrong hash:" + hashstr);

                            hash = hash.Reverse().ToArray();
                            return true;
                        }
                        catch
                        {
                            throw new Exception("hex format error:" + hashstr);
                        }
                    }
                    else
                    {

                        if (!(a.Value is Mono.Cecil.CustomAttributeArgument[] list) || list.Length < 20)
                        {
                            throw new Exception("hash too short.");
                        }
                        hash = new byte[20];
                        for (var i = 0; i < 20; i++)
                        {
                            hash[i] = (byte)list[i].Value;
                        }
                        hash = hash.Reverse().ToArray();
                        return true;
                    }
                }
            }
            hash = null;
            return false;
        }

        public bool IsNonCall(Mono.Cecil.MethodDefinition defs)
        {
            if (defs == null)
            {
                return false;
            }
            foreach (var attr in defs.CustomAttributes)
            {
                if (attr.AttributeType.Name == "NonemitAttribute")
                {
                    return true;
                }
                if (attr.AttributeType.Name == "NonemitWithConvertAttribute")
                {
                    throw new Exception("NonemitWithConvert func only used for readonly static field.");
                }
                if (attr.AttributeType.Name == "ScriptAttribute")
                {
                    var strv = attr.ConstructorArguments[0].Value as string;
                    if (string.IsNullOrEmpty(strv))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool IsMixAttribute(Mono.Cecil.MethodDefinition defs, out VM.OpCode[] opcodes, out string[] opdata)
        {
            // ============================================
            // Integrates attributes: OpCode/Syscall/Script
            // ============================================

            opcodes = null;
            opdata = null;

            if (defs == null) return false;

            int count_attrs = 0;

            foreach (var attr in defs.CustomAttributes)
            {
                if ((attr.AttributeType.Name == "OpCodeAttribute") ||
                    (attr.AttributeType.Name == "SyscallAttribute") ||
                    (attr.AttributeType.Name == "ScriptAttribute"))
                    count_attrs++;
            }

            if (count_attrs == 0) return false; // no OpCode/Syscall/Script Attribute

            opcodes = new VM.OpCode[count_attrs];
            opdata = new string[count_attrs];

            int i = 0; // index each attribute
            int ext = 0; // extension attributes (automatically included if using 'this' on parameter)

            foreach (var attr in defs.CustomAttributes)
            {
                if (attr.AttributeType.Name == "OpCodeAttribute")
                {
                    opcodes[i] = (VM.OpCode)attr.ConstructorArguments[0].Value;
                    opdata[i] = (string)attr.ConstructorArguments[1].Value;

                    i++;
                }
                else if (attr.AttributeType.Name == "SyscallAttribute")
                {
                    //var type = attr.ConstructorArguments[0].Type;
                    var val = (string)attr.ConstructorArguments[0].Value;

                    opcodes[i] = VM.OpCode.SYSCALL;
                    opdata[i] = val;

                    i++;
                }
                else if (attr.AttributeType.Name == "ScriptAttribute")
                {
                    //var type = attr.ConstructorArguments[0].Type;
                    var val = (string)attr.ConstructorArguments[0].Value;

                    opcodes[i] = VM.OpCode.NOP;
                    opdata[i] = val;

                    i++;
                }

                if (attr.AttributeType.Name == "ExtensionAttribute")
                    ext++;
            }

            if ((count_attrs + ext) == defs.CustomAttributes.Count)
            {
                // all attributes are OpCode or Syscall or Script (plus ExtensionAttribute which is automatic)
                return true;
            }
            else
            {
                // OpCodeAttribute/SyscallAttribute together with different attributes, cannot mix!
                throw new Exception("neomachine Cannot mix OpCode/Syscall/Script attributes with others!");
            }
        }

        /*
                public bool IsOpCall(Mono.Cecil.MethodDefinition defs, out VM.OpCode[] opcodes)
                {
                    opcodes = null;
                    if (defs == null)
                    {
                        return false;
                    }

                    foreach (var attr in defs.CustomAttributes)
                    {
                        if (attr.AttributeType.Name == "OpCodeAttribute")
                        {

                            var type = attr.ConstructorArguments[0].Type;

                            Mono.Cecil.CustomAttributeArgument[] val = (Mono.Cecil.CustomAttributeArgument[])attr.ConstructorArguments[0].Value;

                            opcodes = new VM.OpCode[val.Length];
                            for (var j = 0; j < val.Length; j++)
                            {
                                opcodes[j] = ((VM.OpCode)(byte)val[j].Value);
                            }

                            return true;
                        }
                        //if(attr.t)
                    }
                    return false;
                }
        */

        public bool IsNotifyCall(Mono.Cecil.MethodDefinition defs, Mono.Cecil.MethodReference refs, NeoMethod to, out string name)
        {

            name = to.lastsfieldname;
            if (to.lastsfieldname == null)
                return false;

            Mono.Cecil.TypeDefinition call = null;
            if (defs == null)
            {
                try
                {
                    call = refs.DeclaringType.Resolve();
                }
                catch
                {//In most case it's a template class
                }
            }
            else
            {
                call = defs.DeclaringType;
            }

            if (call != null)
            {
                if (call.BaseType.Name == "MulticastDelegate" || call.BaseType.Name == "Delegate")
                {
                    to.lastsfieldname = null;
                    return true;
                }
            }
            else // Cannot restore type, we only can judge by name
            {
                if (refs.Name == "Invoke" && refs.DeclaringType.Name.Contains("Action`"))
                {
                    to.lastsfieldname = null;
                    return true;
                }
            }
            name = "Notify";
            return false;
        }

        private int _ConvertCall(OpCode src, NeoMethod to)
        {
            Mono.Cecil.MethodReference refs = src.tokenUnknown as Mono.Cecil.MethodReference;

            int calltype = 0;
            string callname = "";
            int callpcount = 0;
            byte[] callhash = null;
            VM.OpCode[] callcodes = null;
            string[] calldata = null;

            Mono.Cecil.MethodDefinition defs = null;
            Exception defError = null;
            try
            {
                defs = refs.Resolve();
            }
            catch (Exception err)
            {
                defError = err;
            }

            if (IsNonCall(defs))
            {
                this.addrconv[src.addr] = addr;

                return 0;
            }
            else if (IsNotifyCall(defs, refs, to, out callname))
            {
                calltype = 5;
            }
            else if (to.lastparam >= 0)
            {
                callpcount = to.lastparam;
                calltype = 6;
                to.lastparam = -1;
            }
            //else if (IsOpCall(defs, out callcodes))
            //{
            //    calltype = 2;

            //if (System.Enum.TryParse<VM.OpCode>(callname, out callcode))
            //{
            //    calltype = 2;
            //}
            //else
            //{
            //    throw new Exception("Can not find OpCall:" + callname);
            //}
            //}
            //else if (IsOpCodesCall(defs, out callcodes))
            //{
            //    calltype = 7;
            //}
            //else if (IsSysCall(defs, out callname))
            //{
            //    calltype = 3;
            //}
            else if (IsMixAttribute(defs, out callcodes, out calldata))
            {
                //only syscall, need to reverse arguments
                //only opcall, no matter what arguments
                calltype = 7;

                if (callcodes.Length == 1 && callcodes[0] != VM.OpCode.SYSCALL)
                {
                    calltype = 2;
                }
            }
            else if (IsAppCall(defs, out callhash))
            {
                calltype = 4;
            }
            else if (this.outModule.mapMethods.ContainsKey(src.tokenMethod))
            {//this is a call
                calltype = 1;
            }
            else
            {//maybe a syscall // or other
                if (src.tokenMethod.Contains("::op_Explicit(") || src.tokenMethod.Contains("::op_Implicit("))
                {
                    //All types of display implicit conversion are ignored
                    //There are some special types that need to be convert
                    if (src.tokenMethod == "System.Int32 System.Numerics.BigInteger::op_Explicit(System.Numerics.BigInteger)")
                    {
                        //donothing
                        return 0;
                    }
                    else if (src.tokenMethod == "System.Int64 System.Numerics.BigInteger::op_Explicit(System.Numerics.BigInteger)")
                    {
                        //donothing
                        return 0;
                    }
                    else if (src.tokenMethod == "System.Numerics.BigInteger System.Numerics.BigInteger::op_Implicit(System.Int32)")//int->bignumber
                    {
                        //donothing
                        return 0;
                    }
                    else if (src.tokenMethod == "System.Numerics.BigInteger System.Numerics.BigInteger::op_Implicit(System.Int64)")
                    {
                        return 0;
                    }

                    return 0;
                }
                else if (src.tokenMethod == "System.Void System.Diagnostics.Debugger::Break()")
                {
                    _Convert1by1(VM.OpCode.NOP, src, to);

                    return 0;
                }
                else if (src.tokenMethod.Contains("::op_Equality(") || src.tokenMethod.Contains("::Equals("))
                {
                    var _ref = src.tokenUnknown as Mono.Cecil.MethodReference;

                    if (_ref.DeclaringType.FullName == "System.Boolean"
                        || _ref.DeclaringType.FullName == "System.Int32"
                        || _ref.DeclaringType.FullName == "System.Numerics.BigInteger")
                    {
                        _Convert1by1(VM.OpCode.NUMEQUAL, src, to);
                    }
                    else
                    {
                        _Convert1by1(VM.OpCode.EQUAL, src, to);

                    }
                    //if (src.tokenMethod == "System.Boolean System.String::op_Equality(System.String,System.String)")
                    //{
                    //    _Convert1by1(VM.OpCode.EQUAL, src, to);
                    //    return 0;
                    //}
                    //else if (src.tokenMethod == "System.Boolean System.Object::Equals(System.Object)")
                    //{
                    //    _Convert1by1(VM.OpCode.EQUAL, src, to);
                    //    return 0;
                    //}
                    //_Convert1by1(VM.OpCode.EQUAL, src, to);
                    return 0;
                }
                else if (src.tokenMethod.Contains("::op_Inequality("))
                {
                    var _ref = src.tokenUnknown as Mono.Cecil.MethodReference;
                    if (_ref.DeclaringType.FullName == "System.Boolean"
                        || _ref.DeclaringType.FullName == "System.Int32"
                        || _ref.DeclaringType.FullName == "System.Numerics.BigInteger")
                    {
                        _Convert1by1(VM.OpCode.NUMNOTEQUAL, src, to);
                    }
                    else
                    {
                        _Convert1by1(VM.OpCode.EQUAL, src, to);
                        _Convert1by1(VM.OpCode.NOT, null, to);
                    }
                    //if (src.tokenMethod == "System.Boolean System.Numerics.BigInteger::op_Inequality(System.Numerics.BigInteger,System.Numerics.BigInteger)")
                    //{
                    //    _Convert1by1(VM.OpCode.INVERT, src, to);
                    //    _Insert1(VM.OpCode.EQUAL, "", to);
                    //    return 0;
                    //}
                    //_Convert1by1(VM.OpCode.INVERT, src, to);
                    //_Insert1(VM.OpCode.EQUAL, "", to);
                    return 0;
                }
                else if (src.tokenMethod.Contains("::op_Addition("))
                {
                    if (src.tokenMethod == "System.Numerics.BigInteger System.Numerics.BigInteger::op_Addition(System.Numerics.BigInteger,System.Numerics.BigInteger)")
                    {
                        _Convert1by1(VM.OpCode.ADD, src, to);
                        return 0;
                    }
                    _Convert1by1(VM.OpCode.ADD, src, to);
                    return 0;
                }
                else if (src.tokenMethod.Contains("::op_Subtraction("))
                {
                    if (src.tokenMethod == "System.Numerics.BigInteger System.Numerics.BigInteger::op_Subtraction(System.Numerics.BigInteger,System.Numerics.BigInteger)")
                    {
                        _Convert1by1(VM.OpCode.SUB, src, to);
                        return 0;
                    }
                    _Convert1by1(VM.OpCode.SUB, src, to);
                    return 0;
                }
                else if (src.tokenMethod.Contains("::op_Multiply("))
                {
                    if (src.tokenMethod == "System.Numerics.BigInteger System.Numerics.BigInteger::op_Multiply(System.Numerics.BigInteger,System.Numerics.BigInteger)")
                    {
                        _Convert1by1(VM.OpCode.MUL, src, to);
                        return 0;
                    }
                    _Convert1by1(VM.OpCode.MUL, src, to);
                    return 0;
                }
                else if (src.tokenMethod.Contains("::op_Division("))
                {
                    if (src.tokenMethod == "System.Numerics.BigInteger System.Numerics.BigInteger::op_Division(System.Numerics.BigInteger, System.Numerics.BigInteger)")
                    {
                        _Convert1by1(VM.OpCode.DIV, src, to);
                        return 0;
                    }
                    _Convert1by1(VM.OpCode.DIV, src, to);
                    return 0;
                }
                else if (src.tokenMethod.Contains("::op_Modulus("))
                {
                    if (src.tokenMethod == "System.Numerics.BigInteger System.Numerics.BigInteger::op_Modulus(System.Numerics.BigInteger,System.Numerics.BigInteger)")
                    {
                        _Convert1by1(VM.OpCode.MOD, src, to);
                        return 0;
                    }
                    _Convert1by1(VM.OpCode.MOD, src, to);
                    return 0;
                }
                else if (src.tokenMethod.Contains("::op_LessThan("))
                {
                    _Convert1by1(VM.OpCode.LT, src, to);
                    return 0;
                }
                else if (src.tokenMethod.Contains("::op_GreaterThan("))
                {
                    _Convert1by1(VM.OpCode.GT, src, to);
                    return 0;
                }
                else if (src.tokenMethod.Contains("::op_LessThanOrEqual("))
                {
                    _Convert1by1(VM.OpCode.LE, src, to);
                    return 0;
                }
                else if (src.tokenMethod.Contains("::op_GreaterThanOrEqual("))
                {
                    _Convert1by1(VM.OpCode.GE, src, to);
                    return 0;
                }
                else if (src.tokenMethod.Contains("::get_Length("))
                {
                    //"System.Int32 System.String::get_Length()"
                    _Convert1by1(VM.OpCode.SIZE, src, to);
                    return 0;
                }
                else if (src.tokenMethod.Contains("::Concat("))
                {
                    //"System.String System.String::Concat(System.String,System.String)"
                    _Convert1by1(VM.OpCode.CAT, src, to);
                    return 0;
                }

                else if (src.tokenMethod == "System.String System.String::Substring(System.Int32,System.Int32)")
                {
                    _Convert1by1(VM.OpCode.SUBSTR, src, to);
                    return 0;

                }
                else if (src.tokenMethod == "System.Char System.String::get_Chars(System.Int32)")
                {
                    _ConvertPush(1, src, to);
                    _Convert1by1(VM.OpCode.SUBSTR, null, to);
                    return 0;
                }
                else if (src.tokenMethod == "System.String System.String::Substring(System.Int32)")
                {
                    throw new Exception("neomachine cant use this call,please use  .SubString(1,2) with 2 params.");
                }
                else if (src.tokenMethod == "System.String System.Char::ToString()")
                {
                    return 0;
                }
                else if (src.tokenMethod == "System.Byte[] System.Numerics.BigInteger::ToByteArray()")
                {
                    return 0;
                }
                else if (src.tokenMethod == "System.Void System.Numerics.BigInteger::.ctor(System.Byte[])")
                {
                    //use slot set before by ldloca
                    _ConvertStLoc(null, src, to, ldloca_slot);
                    ldloca_slot = -1;
                    return 0;
                }
                else if (src.tokenMethod.Contains("::op_LeftShift("))
                {
                    _Convert1by1(VM.OpCode.SHL, src, to);
                    return 0;
                }
                else if (src.tokenMethod.Contains("::op_RightShift("))
                {
                    _Convert1by1(VM.OpCode.SHR, src, to);
                    return 0;
                }
                else
                {
                }
            }

            if (calltype == 0)
            {
                if (defs == null && defError != null)
                {
                    if (defError is Mono.Cecil.AssemblyResolutionException dllError)
                    {
                        logger.Log("<Error>Miss a Symbol in :" + dllError.AssemblyReference.FullName);
                        logger.Log("<Error>Check DLLs for contract.");
                    }
                    throw defError;
                }
                // All previous attempts have been failed, and it's not necessarily a function that doesn't exist, it maybe in another module
                if (TryInsertMethod(outModule, defs))
                {
                    calltype = 1;
                    //ILModule module = new ILModule();
                    //module.LoadModule
                    //ILType type =new ILType()
                    //ILMethod method = new ILMethod(defs)
                }
                else
                    throw new Exception("unknown call: " + src.tokenMethod + "\r\n   in: " + to.name + "\r\n");
            }
            var md = src.tokenUnknown as Mono.Cecil.MethodReference;
            var pcount = md.Parameters.Count;
            bool havethis = md.HasThis;
            if (calltype == 2)
            {
                //opcode call
            }
            else
            {// reverse the arguments order

                //this become very diffcult

                // because opcode donot need to flip params
                // but syscall need
                // calltype7 is  opcode? or is syscall?

                // i will make calltype7 =calltype3 , you can add flip opcode if you need.
                if (havethis && calltype == 7) // is syscall
                    pcount++;
                //if ((calltype == 3) || ((calltype == 7) && (callcodes[0] == VM.OpCode.SYSCALL)))
                //    pcount++;
                // calltype == 3 does not exist anymore

                _Convert1by1(VM.OpCode.NOP, src, to);
                if (pcount <= 1)
                {
                }
                else if (pcount == 2)
                {
                    _Insert1(VM.OpCode.SWAP, "swap 2 param", to);
                }
                else if (pcount == 3)
                {
                    _Insert1(VM.OpCode.REVERSE3, "", to);
                }
                else if (pcount == 4)
                {
                    _Insert1(VM.OpCode.REVERSE4, "", to);
                }
                else
                {
                    _InsertPush(pcount, "swap" + pcount, to);
                    _Insert1(VM.OpCode.REVERSEN, "", to);
                }
            }
            if (calltype == 1)
            {
                var c = _Convert1by1(VM.OpCode.CALL_L, null, to, new byte[] { 5, 0, 0, 0 });
                c.needfixfunc = true;
                c.srcfunc = src.tokenMethod;
                return 0;
            }

            else if (calltype == 2)
            {
                _Convert1by1(callcodes[0], src, to, Helper.OpDataToBytes(calldata[0]));
            }

            /*
                        else if (calltype == 3)
                        {
                            byte[] bytes = null;
                            if (this.outModule.option.useSysCallInteropHash)
                            {
                                //now neovm use ineropMethod hash for syscall.
                                bytes = BitConverter.GetBytes(callname.ToInteropMethodHash());
                            }
                            else
                            {
                                bytes = System.Text.Encoding.UTF8.GetBytes(callname);
                                if (bytes.Length > 252) throw new Exception("string is to long");
                            }
                            byte[] outbytes = new byte[bytes.Length + 1];
                            outbytes[0] = (byte)bytes.Length;
                            Array.Copy(bytes, 0, outbytes, 1, bytes.Length);
                            //bytes.Prepend 函数在 dotnet framework 4.6 编译不过
                            _Convert1by1(VM.OpCode.SYSCALL, null, to, outbytes);
                            return 0;
                        }
            */
            else if (calltype == 7)
            {
                for (var j = 0; j < callcodes.Length; j++)
                {
                    if (callcodes[j] == VM.OpCode.SYSCALL)
                    {
                        byte[] bytes = BitConverter.GetBytes(calldata[j].ToInteropMethodHash());
                        _Convert1by1(VM.OpCode.SYSCALL, null, to, bytes);
                    }
                    else
                    {
                        byte[] opdata = Helper.OpDataToBytes(calldata[j]);

                        _Convert1by1(callcodes[j], src, to, opdata);
                    }
                }
                return 0;
            }
            else if (calltype == 4)
            {
                _ConvertPush(callhash, src, to);
                _Insert1(VM.OpCode.SYSCALL, "", to, BitConverter.GetBytes(InteropService.Contract.Call));
            }
            else if (calltype == 5)
            {
                var callp = Encoding.UTF8.GetBytes(callname);
                _ConvertPush(callp, src, to);

                // package the arguments into an array
                _ConvertPush(pcount + 1, null, to);
                _Convert1by1(VM.OpCode.PACK, null, to);

                //a syscall
                {
                    var bytes = BitConverter.GetBytes(InteropService.Runtime.Notify);
                    //byte[] outbytes = new byte[bytes.Length + 1];
                    //outbytes[0] = (byte)bytes.Length;
                    //Array.Copy(bytes, 0, outbytes, 1, bytes.Length);
                    //bytes.Prepend can't be compiled in dotnet framework 4.6
                    _Convert1by1(VM.OpCode.SYSCALL, null, to, bytes);
                }
            }
            else if (calltype == 6)
            {
                _ConvertPush(callpcount, src, to);
                _Convert1by1(VM.OpCode.ROLL, null, to);
                _Convert1by1(VM.OpCode.SYSCALL, null, to, BitConverter.GetBytes(InteropService.Contract.Call));
            }
            return 0;
        }

        private int _ConvertStringSwitch(ILMethod method, OpCode src, NeoMethod to)
        {
            var lastaddr = method.GetLastCodeAddr(src.addr);
            var nextaddr = method.GetNextCodeAddr(src.addr);
            OpCode last = method.body_Codes[lastaddr];
            OpCode next = method.body_Codes[nextaddr];
            var bLdLoc = (last.code == CodeEx.Ldloc || last.code == CodeEx.Ldloc_0 || last.code == CodeEx.Ldloc_1 || last.code == CodeEx.Ldloc_2 || last.code == CodeEx.Ldloc_3 || last.code == CodeEx.Ldloc_S);
            var bLdArg = (last.code == CodeEx.Ldarg || last.code == CodeEx.Ldarg_0 || last.code == CodeEx.Ldarg_1 || last.code == CodeEx.Ldarg_2 || last.code == CodeEx.Ldarg_3 || last.code == CodeEx.Ldarg_S);
            var bStLoc = (next.code == CodeEx.Stloc || next.code == CodeEx.Stloc_0 || next.code == CodeEx.Stloc_1 || next.code == CodeEx.Stloc_2 || next.code == CodeEx.Stloc_3 || next.code == CodeEx.Stloc_S);
            if (bLdLoc && bStLoc && last.tokenI32 != next.tokenI32)
            {
                //use temp var for switch
            }
            else if (bLdArg && bStLoc)
            {
                //use arg for switch
            }
            else
            {//not parse this type of code yet.
                throw new Exception("not use a temp loc,not parse this type of code yet.");
            }

            int skipcount = 1;
            bool isjumptable = false;
            var jumptableaddr = method.GetNextCodeAddr(nextaddr);
            int jumptablecount = 0;
            int brcount = 0;
            do
            {
                OpCode code1 = method.body_Codes[jumptableaddr];
                if (code1.code == CodeEx.Ret || code1.code == CodeEx.Br || code1.code == CodeEx.Br_S)
                {
                    isjumptable = true;
                    jumptableaddr = method.GetNextCodeAddr(jumptableaddr);
                    skipcount++;
                    brcount++;
                }
                else
                {
                    OpCode code2 = method.body_Codes[method.GetNextCodeAddr(jumptableaddr)];
                    OpCode code3 = method.body_Codes[method.GetNextCodeAddr(code2.addr)];
                    var bLdLoc1 = (code1.code == CodeEx.Ldloc || code1.code == CodeEx.Ldloc_0 || code1.code == CodeEx.Ldloc_1 || code1.code == CodeEx.Ldloc_2 || code1.code == CodeEx.Ldloc_3 || code1.code == CodeEx.Ldloc_S);
                    var bLdArg1 = (code1.code == CodeEx.Ldarg || code1.code == CodeEx.Ldarg_0 || code1.code == CodeEx.Ldarg_1 || code1.code == CodeEx.Ldarg_2 || code1.code == CodeEx.Ldarg_3 || code1.code == CodeEx.Ldarg_S);
                    var bLdC4 = code2.code == CodeEx.Ldc_I4 || code2.code == CodeEx.Ldc_I4_0 || code2.code == CodeEx.Ldc_I4_1 || code2.code == CodeEx.Ldc_I4_2 || code2.code == CodeEx.Ldc_I4_3
                        || code2.code == CodeEx.Ldc_I4_4 || code2.code == CodeEx.Ldc_I4_5 || code2.code == CodeEx.Ldc_I4_6 || code2.code == CodeEx.Ldc_I4_7 || code2.code == CodeEx.Ldc_I4_8
                        || code2.code == CodeEx.Ldc_I4_M1 || code2.code == CodeEx.Ldc_I4_S;
                    var bJmp = code3.code == CodeEx.Beq || code3.code == CodeEx.Beq_S || code3.code == CodeEx.Bge || code3.code == CodeEx.Bge_S || code3.code == CodeEx.Bge_Un || code3.code == CodeEx.Bge_Un_S
                        || code3.code == CodeEx.Bgt || code3.code == CodeEx.Bgt_S || code3.code == CodeEx.Bgt_Un || code3.code == CodeEx.Bgt_Un_S
                        || code3.code == CodeEx.Ble || code3.code == CodeEx.Ble_S || code3.code == CodeEx.Ble_Un || code3.code == CodeEx.Ble_Un_S
                        || code3.code == CodeEx.Blt || code3.code == CodeEx.Blt_S || code3.code == CodeEx.Blt_Un || code3.code == CodeEx.Blt_Un_S
                        || code3.code == CodeEx.Bne_Un || code3.code == CodeEx.Bne_Un_S;
                    if (bLdLoc1 && bLdC4 && bJmp)
                    {
                        isjumptable = true;
                        jumptableaddr = method.GetNextCodeAddr(code3.addr);
                        skipcount += 3;
                        jumptablecount++;
                    }
                    else
                    {
                        isjumptable = false;
                        // check it whether in ldstr
                        if (bLdLoc1 && code1.tokenI32 == last.tokenI32 && code2.code == CodeEx.Ldstr && (code3.code == CodeEx.Call || code3.code == CodeEx.Callvirt) && code3.tokenMethod.Is_String_op_Equality())
                        {
                            //is switch ldstr with ldloc
                        }
                        else if (bLdArg1 && code1.tokenI32 == last.tokenI32 && code2.code == CodeEx.Ldstr && (code3.code == CodeEx.Call || code3.code == CodeEx.Callvirt) && code3.tokenMethod.Contains("String::op_Equality"))
                        {
                            //is switch ldstr with ldarg
                        }
                        else
                        {
                            throw new Exception("unknown switch info");
                            //is not
                        }
                    }
                }
            }
            while (isjumptable);

            // handle jumpstr
            bool isjumpstr = false;
            do
            {
                OpCode code1 = method.body_Codes[jumptableaddr];
                OpCode code2 = method.body_Codes[method.GetNextCodeAddr(jumptableaddr)];
                OpCode code3 = method.body_Codes[method.GetNextCodeAddr(code2.addr)];
                OpCode code4 = method.body_Codes[method.GetNextCodeAddr(code3.addr)];
                //ldstr with ldloc
                var bLdLoc1 = (code1.code == CodeEx.Ldloc || code1.code == CodeEx.Ldloc_0 || code1.code == CodeEx.Ldloc_1 || code1.code == CodeEx.Ldloc_2 || code1.code == CodeEx.Ldloc_3 || code1.code == CodeEx.Ldloc_S)
                     && code1.tokenI32 == last.tokenI32;
                //ldstr with ldarg :release and switch with a function param
                var bLdArg1 = (code1.code == CodeEx.Ldarg || code1.code == CodeEx.Ldarg_0 || code1.code == CodeEx.Ldarg_1 || code1.code == CodeEx.Ldarg_2 || code1.code == CodeEx.Ldarg_3 || code1.code == CodeEx.Ldarg_S)
                     && code1.tokenI32 == last.tokenI32;

                var bLDStr2 = code2.code == CodeEx.Ldstr;
                var bCallStrEq3 = (code3.code == CodeEx.Call || code3.code == CodeEx.Callvirt) && code3.tokenMethod.Is_String_op_Equality();
                var bBRTrue4 = code4.code == CodeEx.Brtrue || code4.code == CodeEx.Brtrue_S;
                if ((bLdLoc1 || bLdArg1) && bLDStr2 && bCallStrEq3 && bBRTrue4)
                {
                    isjumpstr = true;

                    skipcount += ConvertCode(method, code1, to);
                    skipcount += ConvertCode(method, code2, to);
                    skipcount += ConvertCode(method, code3, to);
                    skipcount += ConvertCode(method, code4, to);
                    //is switch ldstr
                    var code5 = method.body_Codes[method.GetNextCodeAddr(code4.addr)];
                    if (code5.code == CodeEx.Ret || code5.code == CodeEx.Br || code5.code == CodeEx.Br_S)
                    {
                        //code5 is a jmp instruction
                        skipcount++;
                        jumptableaddr = method.GetNextCodeAddr(code5.addr);
                    }
                    else
                    {
                        jumptableaddr = code5.addr;
                    }
                }
                else
                {
                    isjumpstr = false; //end handling jmp
                }
            }
            while (isjumpstr);

            //There will be more than six jump table paragraphs after that
            //The feature is a set of three instructions
            //ldloc =last
            //ldc.i4
            //condition instruction: bgt bet

            //The return segment may also appear

            //Found those segments. If the total segments is less than 6, then it's not a swith instruction


            //After that, it'll enter the string comparing segments
            //ldloc =last
            //ldstr
            //call String::op_Equality(string, string)
            //brtrue condition jmp

            //Found the segment, then delete the jump table
            return skipcount;
        }

        private bool TryInsertMethod(NeoModule outModule, Mono.Cecil.MethodDefinition method)
        {
            var oldaddr = this.addr;
            var oldaddrconv = new Dictionary<int, int>();
            foreach (int k in addrconv.Keys)
            {
                oldaddrconv[k] = addrconv[k];
            }
            var typename = method.DeclaringType.FullName;
            if (inModule.mapType.TryGetValue(typename, out ILType type) == false)
            {
                type = new ILType(null, method.DeclaringType, logger);
                inModule.mapType[typename] = type;
            }

            var _method = type.methods[method.FullName];
            try
            {
                if (method.Is_cctor())
                {
                    CctorSubVM.Parse(_method, this.outModule);
                    //continue;
                    return false;
                }
                if (method.Is_ctor())
                {
                    return false;
                    //continue;
                }

                NeoMethod nm = new NeoMethod(_method);
                this.methodLink[_method] = nm;
                outModule.mapMethods[nm.name] = nm;
                ConvertMethod(_method, nm);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                this.addr = oldaddr;
                this.addrconv.Clear();
                foreach (int k in oldaddrconv.Keys)
                {
                    addrconv[k] = oldaddrconv[k];
                }
            }
        }

        private int _ConvertCgt(ILMethod method, OpCode src, NeoMethod to)
        {
            var code = to.body_Codes.Last().Value;
            if (code.code == VM.OpCode.PUSHNULL)
            {
                //remove last code
                to.body_Codes.Remove(code.addr);
                this.addr = code.addr;
                _Convert1by1(VM.OpCode.ISNULL, src, to);
                _Convert1by1(VM.OpCode.NOT, src, to);
            }
            else
            {
                _Convert1by1(VM.OpCode.GT, src, to);
            }
            return 0;
        }

        private int _ConvertCeq(ILMethod method, OpCode src, NeoMethod to)
        {
            var code = to.body_Codes.Last().Value;
            if (code.code == VM.OpCode.PUSHNULL)
            {
                //remove last code
                to.body_Codes.Remove(code.addr);
                this.addr = code.addr;
                _Convert1by1(VM.OpCode.ISNULL, src, to);
            }
            else
            {
                _Convert1by1(VM.OpCode.NUMEQUAL, src, to);
            }
            return 0;
        }

        private int _ConvertNewArr(ILMethod method, OpCode src, NeoMethod to)
        {
            var type = src.tokenType;
            if ((type != "System.Byte") && (type != "System.SByte"))
            {
                _Convert1by1(VM.OpCode.NEWARRAY, src, to);
                int n = method.GetNextCodeAddr(src.addr);
                int n2 = method.GetNextCodeAddr(n);
                int n3 = method.GetNextCodeAddr(n2);
                if (n >= 0 && n2 >= 0 && n3 >= 0 && method.body_Codes[n].code == CodeEx.Dup && method.body_Codes[n2].code == CodeEx.Ldtoken && method.body_Codes[n3].code == CodeEx.Call)
                {
                    var data = method.body_Codes[n2].tokenUnknown as byte[];
                    if (type == "System.Char")
                    {
                        for (var i = 0; i < data.Length; i += 2)
                        {
                            char info = BitConverter.ToChar(data, i);
                            _Convert1by1(VM.OpCode.DUP, null, to);
                            _ConvertPush(i / 2, null, to);
                            _ConvertPush(info, null, to);
                            _Convert1by1(VM.OpCode.SETITEM, null, to);
                        }
                        return 3;
                    }
                    else if (type == "System.UInt32")
                    {
                        for (var i = 0; i < data.Length; i += 4)
                        {
                            var info = BitConverter.ToUInt32(data, i);
                            _Convert1by1(VM.OpCode.DUP, null, to);
                            _ConvertPush(i / 4, null, to);
                            _ConvertPush(info, null, to);
                            _Convert1by1(VM.OpCode.SETITEM, null, to);
                        }
                        return 3;
                    }
                    else if (type == "System.Int32")
                    {
                        for (var i = 0; i < data.Length; i += 4)
                        {
                            var info = BitConverter.ToInt32(data, i);
                            _Convert1by1(VM.OpCode.DUP, null, to);
                            _ConvertPush(i / 4, null, to);
                            _ConvertPush(info, null, to);
                            _Convert1by1(VM.OpCode.SETITEM, null, to);
                        }
                        return 3;
                    }
                    else if (type == "System.Int64")
                    {
                        for (var i = 0; i < data.Length; i += 8)
                        {
                            var info = BitConverter.ToInt64(data, i);
                            _Convert1by1(VM.OpCode.DUP, null, to);
                            _ConvertPush(i / 8, null, to);
                            _ConvertPush(info, null, to);
                            _Convert1by1(VM.OpCode.SETITEM, null, to);
                        }
                        return 3;
                    }
                    else if (type == "System.UInt64")
                    {
                        for (var i = 0; i < data.Length; i += 8)
                        {
                            var info = (System.Numerics.BigInteger)BitConverter.ToUInt64(data, i);
                            _Convert1by1(VM.OpCode.DUP, null, to);
                            _ConvertPush(i / 8, null, to);
                            _ConvertPush(info.ToByteArray(), null, to);
                            _Convert1by1(VM.OpCode.SETITEM, null, to);
                        }
                        return 3;
                    }
                    throw new Exception($"not support this type's init array. type: {type}");

                }
                return 0;
            }
            else
            {
                var code = to.body_Codes.Last().Value;

                if (code.code > VM.OpCode.PUSH16) //we need a number
                    throw new Exception("_ConvertNewArr::not support var lens for new byte[?].");

                var number = getNumber(code);

                to.body_Codes.Remove(code.addr);
                this.addr = code.addr;

                int n = method.GetNextCodeAddr(src.addr);
                int n2 = method.GetNextCodeAddr(n);
                int n3 = method.GetNextCodeAddr(n2);
                _ = method.GetNextCodeAddr(n3);

                if (n >= 0 && n2 >= 0 && n3 >= 0 && method.body_Codes[n].code == CodeEx.Dup && method.body_Codes[n2].code == CodeEx.Ldtoken && method.body_Codes[n3].code == CodeEx.Call)
                {
                    // This is the initialization array

                    // System.Byte or System.SByte
                    var data = method.body_Codes[n2].tokenUnknown as byte[];
                    this._ConvertPush(data, src, to);

                    return 3;
                }
                else
                {
                    var outbyte = new byte[number];
                    var skip = 0;
                    int start = n;
                    var _code = method.body_Codes[start];

                    if (_code.code == CodeEx.Dup) // Replace setlem by dup
                    {
                        while (true)
                        {
                            int start2 = method.GetNextCodeAddr(start);
                            int start3 = method.GetNextCodeAddr(start2);
                            int start4 = method.GetNextCodeAddr(start3);
                            if (start < 0 || start2 < 0 || start3 < 0 || start4 < 0)
                                break;

                            _code = method.body_Codes[start];
                            var _code2 = method.body_Codes[start2];
                            var _code3 = method.body_Codes[start3];
                            var _code4 = method.body_Codes[start4];
                            if (_code.code != CodeEx.Dup || (_code4.code != CodeEx.Stelem_I1 && _code4.code != CodeEx.Stelem_I))
                            {
                                break;
                            }
                            else
                            {
                                var pos = _code2.tokenI32;
                                var value = _code3.tokenI32;
                                outbyte[pos] = (byte)value;

                                skip += 4;
                                start = method.GetNextCodeAddr(start4);
                            }
                        }
                    }
                    else if ((_code.code == CodeEx.Stloc || _code.code == CodeEx.Stloc_0 || _code.code == CodeEx.Stloc_1 || _code.code == CodeEx.Stloc_2 || _code.code == CodeEx.Stloc_3 || _code.code == CodeEx.Stloc_S))
                    {
                        skip++;
                        start = method.GetNextCodeAddr(start);
                        _code = method.body_Codes[start];
                        bool bLdLoc = (_code.code == CodeEx.Ldloc || _code.code == CodeEx.Ldloc_0 || _code.code == CodeEx.Ldloc_1 || _code.code == CodeEx.Ldloc_2 || _code.code == CodeEx.Ldloc_3 || _code.code == CodeEx.Ldloc_S);
                        if (bLdLoc == false)//It means there's no initialization at all
                        {
                            this._ConvertPush(outbyte, src, to);
                            return 0;
                        }
                        while (true)
                        {
                            int start2 = method.GetNextCodeAddr(start);
                            int start3 = method.GetNextCodeAddr(start2);
                            int start4 = method.GetNextCodeAddr(start3);
                            if (start < 0 || start2 < 0 || start3 < 0 || start4 < 0)
                                break;
                            _code = method.body_Codes[start];
                            var _code2 = method.body_Codes[start2];
                            var _code3 = method.body_Codes[start3];
                            var _code4 = method.body_Codes[start4];
                            bLdLoc = (_code.code == CodeEx.Ldloc || _code.code == CodeEx.Ldloc_0 || _code.code == CodeEx.Ldloc_1 || _code.code == CodeEx.Ldloc_2 || _code.code == CodeEx.Ldloc_3 || _code.code == CodeEx.Ldloc_S);
                            bool bStelem = (_code4.code == CodeEx.Stelem_I1 || _code4.code == CodeEx.Stelem_I);

                            if (bLdLoc && bStelem)
                            {
                                var pos = _code2.tokenI32;
                                var value = _code3.tokenI32;
                                outbyte[pos] = (byte)value;

                                skip += 4;
                                start = method.GetNextCodeAddr(start4);
                            }
                            else if (bLdLoc && !bStelem)
                            {
                                //This is not a predictive array initialization, we lost one case for handling
                                this._ConvertPush(outbyte, src, to);
                                // Two cases here
                                if (skip == 1)
                                {
                                    return 0; // Without initialization, the first stloc cannot be skipped
                                }
                                else
                                {
                                    break;
                                }
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                    //Sometimes c# will use the real value for initialization. If the value is byte, it may be an error

                    this._ConvertPush(outbyte, src, to);
                    return skip;
                }
            }
        }

        private int _ConvertInitObj(OpCode src, NeoMethod to)
        {
            var type = (src.tokenUnknown as Mono.Cecil.TypeReference).Resolve();
            _Convert1by1(VM.OpCode.NOP, src, to);
            _ConvertPush(type.Fields.Count, null, to);
            if (type.IsValueType)
            {
                _Insert1(VM.OpCode.NEWSTRUCT, null, to);
            }
            else
            {
                _Insert1(VM.OpCode.NEWARRAY, null, to);
            }
            //use slot set before by ldloca
            _ConvertStLoc(null, src, to, ldloca_slot);
            ldloca_slot = -1;

            return 0;
        }

        private int _ConvertNewObj(OpCode src, NeoMethod to)
        {
            var _type = (src.tokenUnknown as Mono.Cecil.MethodReference);
            if (_type.FullName == "System.Void System.Numerics.BigInteger::.ctor(System.Byte[])")
            {
                return 0;//donothing;
            }
            else if (_type.DeclaringType.FullName.Contains("Exception"))
            {
                _Convert1by1(VM.OpCode.NOP, src, to);
                var pcount = _type.Parameters.Count;
                for (var i = 0; i < pcount; i++)
                {
                    _Insert1(VM.OpCode.DROP, "", to);
                }
                return 0;
            }
            var type = _type.Resolve();

            //Replace the New Array operation if there is an [OpCode] on the constructor
            foreach (var m in type.DeclaringType.Methods)
            {
                if (m.IsConstructor && m.HasCustomAttributes)
                {
                    foreach (var attr in m.CustomAttributes)
                    {
                        if (attr.AttributeType.Name == "OpCodeAttribute")
                        {
                            //object[] op = method.method.Annotations[0] as object[];
                            var opcode = (VM.OpCode)attr.ConstructorArguments[0].Value;
                            var opdata = Helper.OpDataToBytes((string)attr.ConstructorArguments[1].Value);
                            VM.OpCode v = (VM.OpCode)opcode;
                            _Convert1by1(v, src, to, opdata);

                            return 0;
                        }
                    }
                }
            }

            _Convert1by1(VM.OpCode.NOP, src, to);
            _ConvertPush(type.DeclaringType.Fields.Count, null, to);
            if (type.DeclaringType.IsValueType)
            {
                _Insert1(VM.OpCode.NEWSTRUCT, null, to);
            }
            else
            {
                _Insert1(VM.OpCode.NEWARRAY, null, to);
            }
            return 0;
        }

        private int _ConvertStfld(ILMethod method, OpCode src, NeoMethod to)
        {
            var field = (src.tokenUnknown as Mono.Cecil.FieldReference).Resolve();
            var type = field.DeclaringType;
            var id = type.Fields.IndexOf(field);
            if (id < 0)
                throw new Exception("impossible.");

            //_Convert1by1(VM.OpCode.CLONESTRUCTONLY, src, to);

            _ConvertPush(id, null, to);//index
            _Convert1by1(VM.OpCode.SWAP, null, to);//put item top

            _Convert1by1(VM.OpCode.SETITEM, null, to);//moidfy //item //index //array
            return 0;
        }

        private int _ConvertLdfld(OpCode src, NeoMethod to)
        {
            var field = (src.tokenUnknown as Mono.Cecil.FieldReference).Resolve();
            var type = field.DeclaringType;
            var id = type.Fields.IndexOf(field);
            if (id < 0)
                throw new Exception("impossible.");
            _ConvertPush(id, src, to);
            _Convert1by1(VM.OpCode.PICKITEM, null, to);

            return 0;
        }
    }
}
