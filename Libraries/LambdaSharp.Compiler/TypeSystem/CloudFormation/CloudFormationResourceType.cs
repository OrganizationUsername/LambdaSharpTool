/*
 * LambdaSharp (λ#)
 * Copyright (C) 2018-2020
 * lambdasharp.net
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using LambdaSharp.CloudFormation.Specification;

namespace LambdaSharp.Compiler.TypeSystem.CloudFormation {

    internal sealed class CloudFormationResourceType : IResourceType {

        //--- Fields ---
        private readonly ResourceType _resourceType;
        private readonly ExtendedCloudFormationSpecification _specification;
        private readonly Lazy<IEnumerable<IProperty>> _requiredProperties;

        //--- Constructors ---
        public CloudFormationResourceType(string resourceName, ResourceType resourceType, ExtendedCloudFormationSpecification specification) {
            Name = resourceName ?? throw new ArgumentNullException(nameof(resourceName));
            _resourceType = resourceType ?? throw new ArgumentNullException(nameof(resourceType));
            _specification = specification ?? throw new ArgumentNullException(nameof(specification));
            _requiredProperties = new Lazy<IEnumerable<IProperty>>(() => _resourceType.Properties
                .Where(kv => kv.Value.Required).Select(kv => new CloudFormationProperty(kv.Key, this, kv.Value, _specification))
                .ToList()
            );
        }

        //--- Properties ---
        public string Name { get; }

        public IEnumerable<IProperty> RequiredProperties => _requiredProperties.Value;

        //--- Methods ---
        public bool TryGetProperty(string propertyName, [NotNullWhen(true)] out IProperty? property) {
            if(_resourceType.Properties.TryGetValue(propertyName, out var type)) {
                property = new CloudFormationProperty(propertyName, this, type, _specification);
                return true;
            }
            property = null;
            return false;
        }
    }
}