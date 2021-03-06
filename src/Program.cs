﻿using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Origami
{
    internal static class Program
    {
        private static byte[] payload;

        private static void Main( string[] args )
        {
            var file = "";

            Console.WriteLine( "Origami by drakonia - https://github.com/dr4k0nia/Origami \r\n" );
            if ( args.Length == 0 )
            {
                Console.WriteLine( "Usage: Origami.exe <file> or Origami.exe -inject <host> <payload>" );
                Console.ReadKey();
                return;
            }

            file = args[0];

            if ( !File.Exists( file ) )
                throw new FileNotFoundException( $"Could not find file: {file}" );

            var originModule = ModuleDefMD.Load( file );

            if ( !Utils.IsExe( originModule ) )
                throw new Exception( "Invalid file format => supported are .net executables" );

            //input file as payload
            payload = File.ReadAllBytes( file );

            //Generate stub based on origin file
            Console.WriteLine( "Generating new stub module" );
            ModuleDefUser stubModule = CreateStub( originModule );

            ModifyModule( stubModule, false );


            //Rename Global Constructor
            var moduleGlobalType = stubModule.GlobalType;
            moduleGlobalType.Name = "Origami";

            var writerOptions = new ModuleWriterOptions( stubModule );

            writerOptions.WriterEvent += OnWriterEvent;

            stubModule.Write( file.Replace( ".exe", "_origami.exe" ), writerOptions );

            Console.WriteLine( "Saving module..." );
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine( "Finished" );

            Console.ReadKey();
        }

        private static byte[] Compress( byte[] data )
        {
            using ( var mStream = new MemoryStream() )
            {
                using ( var dStream = new DeflateStream( mStream, CompressionLevel.Optimal ) )
                    dStream.Write( data, 0, data.Length );
                return mStream.ToArray();
            }
        }

        private static void OnWriterEvent( object sender, ModuleWriterEventArgs e )
        {
            var writer = (ModuleWriterBase) sender;
            if ( e.Event == ModuleWriterEvent.PESectionsCreated )
            {
                var section = new PESection( ".origami", 0xC0000080 /*0x40000080*/ );

                writer.AddSection( section );

                Console.WriteLine( "Created new pe section {0} with characteristics {1}", section.Name,
                    section.Characteristics.ToString( "x8" ) );

                section.Add( new ByteArrayChunk( Compress( payload ) ), 4 );

                Console.WriteLine( "Wrote {0} bytes to section {1}", payload.Length.ToString(), section.Name );
            }
        }

        #region "Binary modifications"

        private static ModuleDefUser CreateStub( ModuleDef originModule )
        {
            var stubModule =
                new ModuleDefUser( originModule.Name, originModule.Mvid, originModule.CorLibTypes.AssemblyRef );

            originModule.Assembly.Modules.Insert( 0, stubModule );
            ImportAssemblyTypeReferences( originModule, stubModule );

            stubModule.Characteristics = originModule.Characteristics;
            stubModule.Cor20HeaderFlags = originModule.Cor20HeaderFlags;
            stubModule.Cor20HeaderRuntimeVersion = originModule.Cor20HeaderRuntimeVersion;
            stubModule.DllCharacteristics = originModule.DllCharacteristics;
            stubModule.EncBaseId = originModule.EncBaseId;
            stubModule.EncId = originModule.EncId;
            stubModule.Generation = originModule.Generation;
            stubModule.Kind = originModule.Kind;
            stubModule.Machine = originModule.Machine;
            stubModule.TablesHeaderVersion = originModule.TablesHeaderVersion;
            stubModule.Win32Resources = originModule.Win32Resources;
            stubModule.RuntimeVersion = originModule.RuntimeVersion;
            stubModule.Is32BitRequired = originModule.Is32BitRequired;
            stubModule.Is32BitPreferred = originModule.Is32BitPreferred;

            return stubModule;
        }

        //Taken from ConfuserEx (Compressor)
        private static void ImportAssemblyTypeReferences( ModuleDef originModule, ModuleDef stubModule )
        {
            var assembly = stubModule.Assembly;
            foreach ( var ca in assembly.CustomAttributes.Where( ca => ca.AttributeType.Scope == originModule ) )
                ca.Constructor = (ICustomAttributeType) stubModule.Import( ca.Constructor );
            foreach ( var ca in assembly.DeclSecurities.SelectMany( declSec => declSec.CustomAttributes ) )
                if ( ca.AttributeType.Scope == originModule )
                    ca.Constructor = (ICustomAttributeType) stubModule.Import( ca.Constructor );
        }

        //Inspired by EOFAntiTamper
        private static void ModifyModule( ModuleDef module, bool callOnly )
        {
            var loaderType = typeof(Loader);
            //Declare module to inject
            var injectModule = ModuleDefMD.Load( loaderType.Module );
            //Get global constructor or create one if it does not already exist
            var global = module.GlobalType.FindOrCreateStaticConstructor();
            //Declare CallLoader as a TypeDef using it's Metadata token
            var injectType = injectModule.ResolveTypeDef( MDToken.ToRID( loaderType.MetadataToken ) );

            //Use ConfuserEx InjectHelper class to inject Loader class into our target, under <Module>
            var members = InjectHelper.Inject( injectType, module.GlobalType, module );


            Console.WriteLine( "Creating EntryPoint for stub {0}", module.GlobalType.Name );
            //Resolve method for the EntryPoint
            var entryPoint = members.OfType<MethodDef>().Single( method => method.Name == "Main" );
            //Set EntryPoint to Main method defined in the Loader class
            module.EntryPoint = entryPoint;

            //Add STAThreadAttribute
            var attrType = module.CorLibTypes.GetTypeRef( "System", "STAThreadAttribute" );
            var ctorSig = MethodSig.CreateInstance( module.CorLibTypes.Void );
            entryPoint.CustomAttributes.Add( new CustomAttribute(
                new MemberRefUser( module, ".ctor", ctorSig, attrType ) ) );

            //Remove.ctor method because otherwise it will
            //lead to Global constructor error( e.g[MD]: Error: Global item( field, method ) must be Static. [token: 0x06000002] / [MD]: Error: Global constructor. [token: 0x06000002] )
            foreach ( var md in module.GlobalType.Methods )
            {
                if ( md.Name != ".ctor" ) continue;
                module.GlobalType.Remove( md );
                break;
            }
        }

        #endregion
    }
}