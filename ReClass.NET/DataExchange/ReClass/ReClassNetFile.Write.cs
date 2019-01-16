﻿using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using ReClassNET.Logger;
using ReClassNET.Nodes;

namespace ReClassNET.DataExchange.ReClass
{
	public partial class ReClassNetFile
	{
		public void Save(string filePath, ILogger logger)
		{
			using (var fs = new FileStream(filePath, FileMode.Create))
			{
				Save(fs, logger);
			}
		}

		public void Save(Stream output, ILogger logger)
		{
			using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
			{
				var dataEntry = archive.CreateEntry(DataFileName);
				using (var entryStream = dataEntry.Open())
				{
					var document = new XDocument(
						new XComment($"{Constants.ApplicationName} {Constants.ApplicationVersion} by {Constants.Author}"),
						new XComment($"Website: {Constants.HomepageUrl}"),
						new XElement(
							XmlRootElement,
							new XAttribute(XmlVersionAttribute, Version2),
							new XAttribute(XmlPlatformAttribute, Constants.Platform),
							new XElement(XmlClassesElement, CreateClassElements(project.Classes, logger)),
							new XElement(XmlCustomDataElement, project.CustomData.Select(kv => new XElement(kv.Key, kv.Value)))
						)
					);

					document.Save(entryStream);
				}
			}
		}

		private static IEnumerable<XElement> CreateClassElements(IEnumerable<ClassNode> classes, ILogger logger)
		{
			Contract.Requires(classes != null);
			Contract.Requires(Contract.ForAll(classes, c => c != null));
			Contract.Requires(logger != null);
			Contract.Ensures(Contract.Result<IEnumerable<XElement>>() != null);

			return classes.Select(c => new XElement(
				XmlClassElement,
				new XAttribute(XmlUuidAttribute, c.Uuid.ToBase64String()),
				new XAttribute(XmlNameAttribute, c.Name ?? string.Empty),
				new XAttribute(XmlCommentAttribute, c.Comment ?? string.Empty),
				new XAttribute(XmlAddressAttribute, c.AddressFormula ?? string.Empty),
				CreateNodeElements(c.Nodes, logger)
			));
		}

		private static IEnumerable<XElement> CreateNodeElements(IEnumerable<BaseNode> nodes, ILogger logger)
		{
			Contract.Requires(nodes != null);
			Contract.Requires(Contract.ForAll(nodes, n => n != null));
			Contract.Requires(logger != null);
			Contract.Ensures(Contract.Result<IEnumerable<XElement>>() != null);

			foreach (var node in nodes)
			{
				var element = CreateNodeElement(node, logger);
				if (element != null)
				{
					yield return element;
				}
			}
		}

		private static XElement CreateNodeElement(BaseNode node, ILogger logger)
		{
			Contract.Requires(node != null);
			Contract.Requires(logger != null);

			var converter = CustomNodeConvert.GetWriteConverter(node);
			if (converter != null)
			{
				// TODO 5
				return converter.CreateElementFromNode(node, logger);
			}

			if (!buildInTypeToStringMap.TryGetValue(node.GetType(), out var typeString))
			{
				logger.Log(LogLevel.Error, $"Skipping node with unknown type: {node.Name}");
				logger.Log(LogLevel.Warning, node.GetType().ToString());

				return null;
			}

			var element = new XElement(
				XmlNodeElement,
				new XAttribute(XmlNameAttribute, node.Name ?? string.Empty),
				new XAttribute(XmlCommentAttribute, node.Comment ?? string.Empty),
				new XAttribute(XmlHiddenAttribute, node.IsHidden.ToString()),
				new XAttribute(XmlTypeAttribute, typeString)
			);

			if (node is BaseWrapperNode wrapperNode)
			{
				if (node is ClassInstanceNode classInstanceNode)
				{
					element.SetAttributeValue(XmlReferenceAttribute, ((ClassNode)classInstanceNode.InnerNode).Uuid.ToBase64String());
				}
				else if (wrapperNode.InnerNode != null)
				{
					element.Add(CreateNodeElement(wrapperNode.InnerNode, logger));
				}
			}

			switch (node)
			{
				case VTableNode vtableNode:
				{
					element.Add(vtableNode.Nodes.Select(n => new XElement(
						XmlMethodElement,
						new XAttribute(XmlNameAttribute, n.Name ?? string.Empty),
						new XAttribute(XmlCommentAttribute, n.Comment ?? string.Empty),
						new XAttribute(XmlHiddenAttribute, n.IsHidden.ToString())
					)));
					break;
				}
				case BaseWrapperArrayNode arrayNode:
				{
					element.SetAttributeValue(XmlCountAttribute, arrayNode.Count);
					break;
				}
				case BaseTextNode textNode:
				{
					element.SetAttributeValue(XmlLengthAttribute, textNode.Length);
					break;
				}
				case BitFieldNode bitFieldNode:
				{
					element.SetAttributeValue(XmlBitsAttribute, bitFieldNode.Bits);
					break;
				}
				case FunctionNode functionNode:
				{
					var uuid = functionNode.BelongsToClass == null ? NodeUuid.Zero : functionNode.BelongsToClass.Uuid;
					element.SetAttributeValue(XmlReferenceAttribute, uuid.ToBase64String());
					element.SetAttributeValue(XmlSignatureAttribute, functionNode.Signature);
					break;
				}
			}

			return element;
		}

		public static void WriteNodes(Stream output, IEnumerable<BaseNode> nodes, ILogger logger)
		{
			Contract.Requires(output != null);
			Contract.Requires(nodes != null);
			Contract.Requires(Contract.ForAll(nodes, n => n != null));
			Contract.Requires(logger != null);

			using (var project = new ReClassNetProject())
			{
				void RecursiveAddClasses(BaseWrapperNode wrapperNode)
				{
					if (!(wrapperNode.ResolveMostInnerNode() is ClassNode classNode) || project.ContainsClass(classNode.Uuid))
					{
						return;
					}

					project.AddClass(classNode);

					foreach (var wrapperNodeChild in classNode.Nodes.OfType<BaseWrapperNode>())
					{
						RecursiveAddClasses(wrapperNodeChild);
					}
				}

				var serialisationClass = new ClassNode(false)
				{
					Name = SerialisationClassName
				};

				project.AddClass(serialisationClass);

				foreach (var node in nodes)
				{
					if (node is ClassNode classNode)
					{
						project.AddClass(classNode);

						continue;
					}

					if (node is BaseWrapperNode wrapperNode)
					{
						RecursiveAddClasses(wrapperNode);
					}

					serialisationClass.AddNode(node);
				}

				var file = new ReClassNetFile(project);
				file.Save(output, logger);
			}
		}
	}
}
