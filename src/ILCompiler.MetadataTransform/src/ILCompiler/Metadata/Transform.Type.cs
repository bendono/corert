// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Internal.Metadata.NativeFormat.Writer;

using Ecma = System.Reflection.Metadata;
using Cts = Internal.TypeSystem;

using Debug = System.Diagnostics.Debug;
using TypeAttributes = System.Reflection.TypeAttributes;

namespace ILCompiler.Metadata
{
    partial class Transform<TPolicy>
    {
        internal EntityMap<Cts.TypeDesc, MetadataRecord> _types =
            new EntityMap<Cts.TypeDesc, MetadataRecord>(EqualityComparer<Cts.TypeDesc>.Default);

        private Action<Cts.MetadataType, TypeDefinition> _initTypeDef;
        private Action<Cts.MetadataType, TypeReference> _initTypeRef;
        private Action<Cts.ArrayType, TypeSpecification> _initSzArray;
        private Action<Cts.ArrayType, TypeSpecification> _initArray;
        private Action<Cts.ByRefType, TypeSpecification> _initByRef;
        private Action<Cts.PointerType, TypeSpecification> _initPointer;
        private Action<Cts.InstantiatedType, TypeSpecification> _initTypeInst;
        private Action<Cts.SignatureTypeVariable, TypeSpecification> _initTypeVar;
        private Action<Cts.SignatureMethodVariable, TypeSpecification> _initMethodVar;

        public override MetadataRecord HandleType(Cts.TypeDesc type)
        {
            Debug.Assert(!IsBlocked(type));

            MetadataRecord rec;
            if (_types.TryGet(type, out rec))
            {
                return rec;
            }

            if (type.IsSzArray)
            {
                var arrayType = (Cts.ArrayType)type;
                rec = _types.Create(arrayType, _initSzArray ?? (_initSzArray = InitializeSzArray));
            }
            else if (type.IsArray)
            {
                var arrayType = (Cts.ArrayType)type;
                rec = _types.Create(arrayType, _initArray ?? (_initArray = InitializeArray));
            }
            else if (type.IsByRef)
            {
                var byRefType = (Cts.ByRefType)type;
                rec = _types.Create(byRefType, _initByRef ?? (_initByRef = InitializeByRef));
            }
            else if (type.IsPointer)
            {
                var pointerType = (Cts.PointerType)type;
                rec = _types.Create(pointerType, _initPointer ?? (_initPointer = InitializePointer));
            }
            else if (type is Cts.SignatureTypeVariable)
            {
                var variable = (Cts.SignatureTypeVariable)type;
                rec = _types.Create(variable, _initTypeVar ?? (_initTypeVar = InitializeTypeVariable));
            }
            else if (type is Cts.SignatureMethodVariable)
            {
                var variable = (Cts.SignatureMethodVariable)type;
                rec = _types.Create(variable, _initMethodVar ?? (_initMethodVar = InitializeMethodVariable));
            }
            else if (type is Cts.InstantiatedType)
            {
                var instType = (Cts.InstantiatedType)type;
                rec = _types.Create(instType, _initTypeInst ?? (_initTypeInst = InitializeTypeInstance));
            }
            else
            {
                var metadataType = (Cts.MetadataType)type;
                if (_policy.GeneratesMetadata(metadataType))
                {
                    rec = _types.Create(metadataType, _initTypeDef ?? (_initTypeDef = InitializeTypeDef));
                }
                else
                {
                    rec = _types.Create(metadataType, _initTypeRef ?? (_initTypeRef = InitializeTypeRef));
                }
            }

            Debug.Assert(rec is TypeDefinition || rec is TypeReference || rec is TypeSpecification);

            return rec;
        }

        private void InitializeSzArray(Cts.ArrayType entity, TypeSpecification record)
        {
            record.Signature = new SZArraySignature
            {
                ElementType = HandleType(entity.ElementType),
            };
        }

        private void InitializeArray(Cts.ArrayType entity, TypeSpecification record)
        {
            record.Signature = new ArraySignature
            {
                ElementType = HandleType(entity.ElementType),
                Rank = entity.Rank,
                // TODO: LowerBounds
                // TODO: Sizes
            };
        }

        private void InitializeByRef(Cts.ByRefType entity, TypeSpecification record)
        {
            record.Signature = new ByReferenceSignature
            {
                Type = HandleType(entity.ParameterType)
            };
        }

        private void InitializePointer(Cts.PointerType entity, TypeSpecification record)
        {
            record.Signature = new PointerSignature
            {
                Type = HandleType(entity.ParameterType)
            };
        }

        private void InitializeTypeVariable(Cts.SignatureTypeVariable entity, TypeSpecification record)
        {
            record.Signature = new TypeVariableSignature
            {
                Number = entity.Index
            };
        }

        private void InitializeMethodVariable(Cts.SignatureMethodVariable entity, TypeSpecification record)
        {
            record.Signature = new MethodTypeVariableSignature
            {
                Number = entity.Index
            };
        }

        private void InitializeTypeInstance(Cts.InstantiatedType entity, TypeSpecification record)
        {
            var sig = new TypeInstantiationSignature
            {
                GenericType = HandleType(entity.GetTypeDefinition()),
            };

            for (int i = 0; i < entity.Instantiation.Length; i++)
            {
                sig.GenericTypeArguments.Add(HandleType(entity.Instantiation[i]));
            }

            record.Signature = sig;
        }

        private TypeReference GetNestedReferenceParent(Cts.MetadataType entity)
        {
            // This special code deals with the metadata format requirement saying that
            // nested type *references* need to have a type *reference* as their containing type.
            // This is potentially in conflict with our other rule that says to always resolve
            // references to their definition records (we are avoiding emitting references
            // to things that have a definition within the same blob to save space).

            Cts.MetadataType containingType = entity.ContainingType;
            MetadataRecord parentRecord = HandleType(containingType);
            TypeReference parentReferenceRecord = parentRecord as TypeReference;
            
            if (parentReferenceRecord != null)
            {
                // Easy case - parent type doesn't have a definition record.
                return parentReferenceRecord;
            }

            // Parent has a type definition record. We need to make a new record that's a reference.
            // We don't bother with interning these because this will be rare and metadata writer
            // will do the interning anyway.
            Debug.Assert(parentRecord is TypeDefinition);

            parentReferenceRecord = new TypeReference
            {
                TypeName = HandleString(containingType.Name),
            };

            if (containingType.ContainingType != null)
            {
                parentReferenceRecord.ParentNamespaceOrType = GetNestedReferenceParent(containingType);
            }
            else
            {
                parentReferenceRecord.ParentNamespaceOrType = HandleNamespaceReference(containingType.Module, containingType.Namespace);
            }

            return parentReferenceRecord;
        }

        private void InitializeTypeRef(Cts.MetadataType entity, TypeReference record)
        {
            Debug.Assert(entity.IsTypeDefinition);

            if (entity.ContainingType != null)
            {
                record.ParentNamespaceOrType = GetNestedReferenceParent(entity);
            }
            else
            {
                record.ParentNamespaceOrType = HandleNamespaceReference(entity.Module, entity.Namespace);
            }

            record.TypeName = HandleString(entity.Name);
        }

        private void InitializeTypeDef(Cts.MetadataType entity, TypeDefinition record)
        {
            Debug.Assert(entity.IsTypeDefinition);

            if (entity.ContainingType != null)
            {
                var enclosingType = (TypeDefinition)HandleType(entity.ContainingType);
                record.EnclosingType = enclosingType;
                enclosingType.NestedTypes.Add(record);

                var namespaceDefinition =
                    HandleNamespaceDefinition(entity.ContainingType.Module, entity.ContainingType.Namespace);
                record.NamespaceDefinition = namespaceDefinition;
            }
            else
            {
                var namespaceDefinition = HandleNamespaceDefinition(entity.Module, entity.Namespace);
                record.NamespaceDefinition = namespaceDefinition;
                namespaceDefinition.TypeDefinitions.Add(record);
            }

            record.Name = HandleString(entity.Name);

            Cts.ClassLayoutMetadata layoutMetadata = entity.GetClassLayout();
            record.Size = checked((uint)layoutMetadata.Size);
            record.PackingSize = checked((ushort)layoutMetadata.PackingSize);
            record.Flags = GetTypeAttributes(entity);

            if (entity.HasBaseType)
            {
                record.BaseType = HandleType(entity.BaseType);
            }

            if (entity.ExplicitlyImplementedInterfaces.Length > 0)
            {
                record.Interfaces.Capacity = entity.ExplicitlyImplementedInterfaces.Length;
                record.Interfaces.AddRange(entity.ExplicitlyImplementedInterfaces
                    .Where(i => !IsBlocked(i))
                    .Select(i => HandleType(i)));
            }

            if (entity.HasInstantiation)
            {
                record.GenericParameters.Capacity = entity.Instantiation.Length;
                foreach (var p in entity.Instantiation)
                    record.GenericParameters.Add(HandleGenericParameter((Cts.GenericParameterDesc)p));
            }

            foreach (var field in entity.GetFields())
            {
                if (_policy.GeneratesMetadata(field))
                {
                    record.Fields.Add(HandleFieldDefinition(field));
                }
            }

            foreach (var method in entity.GetMethods())
            {
                if (_policy.GeneratesMetadata(method))
                {
                    record.Methods.Add(HandleMethodDefinition(method));
                }
            }

            var ecmaEntity = entity as Cts.Ecma.EcmaType;
            if (ecmaEntity != null)
            {
                Ecma.TypeDefinition ecmaRecord = ecmaEntity.MetadataReader.GetTypeDefinition(ecmaEntity.Handle);
                foreach (var property in ecmaRecord.GetProperties())
                {
                    Property prop = HandleProperty(ecmaEntity.EcmaModule, property);
                    if (prop != null)
                        record.Properties.Add(prop);
                }

                // TODO: Events
                // TODO: CustomAttributes
            }

            // TODO: MethodImpls
        }

        private TypeAttributes GetTypeAttributes(Cts.MetadataType type)
        {
            TypeAttributes result;

            var ecmaType = type as Cts.Ecma.EcmaType;
            if (ecmaType != null)
            {
                Ecma.TypeDefinition ecmaRecord = ecmaType.MetadataReader.GetTypeDefinition(ecmaType.Handle);
                result = ecmaRecord.Attributes;
            }
            else
            {
                result = 0;

                if (type.IsExplicitLayout)
                    result |= TypeAttributes.ExplicitLayout;
                if (type.IsSequentialLayout)
                    result |= TypeAttributes.SequentialLayout;
                if (type.IsInterface)
                    result |= TypeAttributes.Interface;
                if (type.IsSealed)
                    result |= TypeAttributes.Sealed;
                if (type.IsBeforeFieldInit)
                    result |= TypeAttributes.BeforeFieldInit;

                // Not set: Abstract, Ansi/Unicode/Auto, HasSecurity, Import, visibility, Serializable,
                //          WindowsRuntime, HasSecurity, SpecialName, RTSpecialName
            }

            return result;
        }
    }
}
