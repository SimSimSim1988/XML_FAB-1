using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace XML_FAB.Services;

public sealed record XmlValidationResult(bool IsValid, IReadOnlyList<string> Messages);

public sealed class XmlSchemaValidationService
{
    private static readonly IReadOnlyDictionary<string, string> FieldNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ExplanatoryNoteNumber"] = "Шифр пояснительной записки",
        ["ExplanatoryNoteYear"] = "Год составления пояснительной записки",
        ["ModificationNumber"] = "Номер изменения",
        ["ModificationDate"] = "Дата изменения",
        ["ModificationNote"] = "Описание изменения",
        ["FullName"] = "Полное наименование организации",
        ["AbbreviatedName"] = "Сокращённое наименование организации",
        ["OGRN"] = "ОГРН",
        ["INN"] = "ИНН",
        ["KPP"] = "КПП",
        ["NOPRIZNumber"] = "Номер в НОПРИЗ",
        ["Surname"] = "Фамилия",
        ["Name"] = "Наименование",
        ["Patronymic"] = "Отчество",
        ["SNILS"] = "СНИЛС",
        ["RegionCode"] = "Код региона",
        ["OKTMOCode"] = "Код ОКТМО",
        ["OKTMOName"] = "Наименование ОКТМО",
        ["City"] = "Город",
        ["Street"] = "Улица",
        ["Building"] = "Дом",
        ["Room"] = "Помещение",
        ["DocType"] = "Тип документа",
        ["DocName"] = "Наименование документа",
        ["DocNumber"] = "Номер документа",
        ["DocDate"] = "Дата документа",
        ["DocIssueAuthor"] = "Организация, выдавшая документ",
        ["DataDescription"] = "Описание дополнительных данных",
        ["FileName"] = "Имя файла",
        ["FileFormat"] = "Формат файла",
        ["FileChecksum"] = "Контрольная сумма файла",
        ["FinanceComment"] = "Комментарий об источниках финансирования",
        ["ConstructionType"] = "Вид работ",
        ["ObjectID"] = "Идентификатор объекта",
        ["SchemaVersion"] = "Версия схемы",
        ["AccessRestriction"] = "Гриф ограничения доступа"
    };

    public XmlValidationResult Validate(string xmlPath, string schemaPath)
    {
        var messages = new List<string>();
        var schemaIsValid = true;
        var documentHasErrors = false;

        try
        {
            // Сохраняем информацию о строках, чтобы связать сообщение валидатора
            // с конкретным элементом и показать введённое в него значение.
            var document = XDocument.Load(xmlPath, LoadOptions.SetLineInfo);

            var schemas = new XmlSchemaSet { XmlResolver = null };
            schemas.ValidationEventHandler += (_, args) =>
            {
                messages.Add(Format(args));
                if (args.Severity == XmlSeverityType.Error)
                    schemaIsValid = false;
            };
            schemas.Add(null, schemaPath);
            schemas.Compile();

            if (!schemaIsValid)
                return new XmlValidationResult(false, messages);

            var settings = new XmlReaderSettings
            {
                ValidationType = ValidationType.Schema,
                Schemas = schemas,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            settings.ValidationFlags |= XmlSchemaValidationFlags.ProcessIdentityConstraints;
            settings.ValidationFlags |= XmlSchemaValidationFlags.ReportValidationWarnings;
            settings.ValidationEventHandler += (_, args) =>
            {
                messages.Add(Format(args, document));
                if (args.Severity == XmlSeverityType.Error)
                    documentHasErrors = true;
            };

            using var reader = XmlReader.Create(xmlPath, settings);
            while (reader.Read()) { }

            return new XmlValidationResult(!documentHasErrors, messages);
        }
        catch (XmlException ex)
        {
            messages.Add($"Ошибка XML (строка {ex.LineNumber}, позиция {ex.LinePosition}): документ содержит синтаксическую ошибку.");
        }
        catch (XmlSchemaException ex)
        {
            messages.Add($"Ошибка XSD (строка {ex.LineNumber}, позиция {ex.LinePosition}): схема содержит ошибку.");
        }
        catch (IOException)
        {
            messages.Add("Ошибка чтения файла XML или XSD. Проверьте путь и доступность файла.");
        }
        catch (UnauthorizedAccessException)
        {
            messages.Add("Нет доступа к файлу XML или XSD. Проверьте права доступа.");
        }

        return new XmlValidationResult(false, messages);
    }

    private static string Format(ValidationEventArgs args, XDocument? document = null)
    {
        var prefix = args.Severity == XmlSeverityType.Error ? "Ошибка" : "Предупреждение";
        var exception = args.Exception;
        var line = exception is { LineNumber: > 0 }
            ? $" (строка {exception.LineNumber}, позиция {exception.LinePosition})"
            : string.Empty;
        var field = exception is { LineNumber: > 0 }
            ? FindFieldAtLine(document, exception.LineNumber)
            : null;

        if (field is not null)
        {
            var valueText = string.IsNullOrWhiteSpace(field.Value)
                ? "значение не указано"
                : $"введено: «{field.Value.Trim()}»";
            return $"{prefix}{line}: поле «{DisplayFieldName(field.Name)}» ({field.Name}) — {valueText}. Проверьте формат и допустимое значение поля по требованиям схемы.";
        }

        return $"{prefix}{line}: XML-документ или схема не соответствуют требованиям проверки.";
    }

    private static (string Name, string Value)? FindFieldAtLine(XDocument? document, int lineNumber)
    {
        if (document?.Root is null)
            return null;

        var elements = document.Root
            .DescendantsAndSelf()
            .Select(element => new
            {
                Element = element,
                Line = ((IXmlLineInfo)element).HasLineInfo() ? ((IXmlLineInfo)element).LineNumber : 0
            })
            .Where(item => item.Line > 0 && item.Line <= lineNumber)
            .ToList();

        if (elements.Count == 0)
            return null;

        // Сначала ищем элемент непосредственно на строке ошибки. Если XSD указала
        // строку внутри многострочного значения — берём ближайшее поле выше по XML.
        var matchingLine = elements.Max(item => item.Line == lineNumber ? item.Line : 0);
        var targetLine = matchingLine > 0 ? matchingLine : elements.Max(item => item.Line);
        var elementsOnLine = elements
            .Where(item => item.Line == targetLine)
            .Select(item => item.Element)
            .OrderByDescending(GetDepth)
            .ToList();

        foreach (var element in elementsOnLine)
        {
            var attribute = element.Attributes().FirstOrDefault(candidate =>
                ((IXmlLineInfo)candidate).HasLineInfo() && ((IXmlLineInfo)candidate).LineNumber == lineNumber);
            if (attribute is not null)
                return (attribute.Name.LocalName, attribute.Value);
        }

        var fieldElement = elementsOnLine.FirstOrDefault(element => !string.IsNullOrWhiteSpace(element.Value))
            ?? elementsOnLine.FirstOrDefault();
        return fieldElement is null ? null : (fieldElement.Name.LocalName, fieldElement.Value);
    }

    private static int GetDepth(XElement element)
    {
        var depth = 0;
        for (var parent = element.Parent; parent is not null; parent = parent.Parent)
            depth++;
        return depth;
    }

    private static string DisplayFieldName(string xmlName)
    {
        if (FieldNames.TryGetValue(xmlName, out var displayName))
            return displayName;

        return Regex.Replace(xmlName, "(?<=[a-z0-9])(?=[A-Z])", " ");
    }
}
