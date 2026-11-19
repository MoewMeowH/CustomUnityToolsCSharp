#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HBLint.Generator;
using HBLint.Generator.Json;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace HBLint.Editor
{
    /// <summary>
    /// rules.json 편집 창. 왼쪽 표는 규칙 목록, 오른쪽 상세 칸은 규칙 종류(어트리뷰트)에서 만든다.
    /// 고칠 때마다 생성기 검증을 메모리에서 돌려 아래에 오류를 보인다. 저장하면 rules.json을 쓰고 자동 생성이 이어진다.
    /// rules.json 구조가 틀리면(형식 오류 · 모르는 칸 등) 편집하지 않는다 — 고칠 수 없는 내용을 저장하면서 잃지 않게.
    /// </summary>
    public sealed class RulesEditorWindow : EditorWindow
    {
        private RuleCatalog? _catalog;
        private RulesDocument? _document;
        private string _rulesPath = "";
        private RuleEntry? _selected;

        private MultiColumnListView? _list;
        private ScrollView? _detail;
        private ScrollView? _errors;
        private Label? _status;
        private ToolbarButton? _duplicateButton;
        private ToolbarButton? _deleteButton;

        // 도메인 리로드(스크립트 컴파일)에도 저장 안 한 편집을 살리려고 직렬화해 둔다
        [SerializeField] private string _pendingJson = "";
        // 연 때 디스크의 rules.json — 저장할 때 밖(git pull · 텍스트 편집기)에서 바뀌었는지 본다
        [SerializeField] private string _loadedText = "";

        public static void Open()
        {
            var window = GetWindow<RulesEditorWindow>();
            window.titleContent = new GUIContent("HB Lint 규칙");
            window.minSize = new Vector2(720, 400);
        }

        private void CreateGUI()
        {
            saveChangesMessage = "rules.json에 저장하지 않은 변경이 있습니다. 저장할까요?";
            Reload();
        }

        public override void SaveChanges()
        {
            if (_document == null || _catalog == null)
                return;
            var errors = ValidationErrors();
            if (errors.Count > 0 && !EditorUtility.DisplayDialog("HB Lint",
                    $"오류 {errors.Count}개가 있습니다. 그래도 저장할까요?\n(생성물은 오류를 고칠 때까지 그대로 둡니다)", "저장", "취소"))
                return;
            if (File.Exists(_rulesPath) && File.ReadAllText(_rulesPath) != _loadedText && !EditorUtility.DisplayDialog("HB Lint",
                    $"{_rulesPath}가 연 뒤에 밖에서 바뀌었습니다. 덮어쓸까요?", "덮어쓰기", "취소"))
                return;

            var text = _document.ToJson(_catalog);
            LintGenerator.WriteAndImport(_rulesPath, text);
            _loadedText = File.ReadAllText(_rulesPath);
            _pendingJson = "";
            base.SaveChanges();
            UpdateStatus();
            // 가져오기 후처리를 기다리지 않고 바로 만든다 (rules.json이 Assets/ 밖이어도 생성되게)
            if (HBLintSettings.instance.AutoGenerate)
                LintGenerator.Generate();
        }

        private void Reload()
        {
            rootVisualElement.Clear();
            hasUnsavedChanges = false;
            _selected = null;
            _rulesPath = HBLintSettings.instance.RulesPath;
            _catalog = LintGenerator.LoadCatalog();
            if (_catalog == null)
            {
                rootVisualElement.Add(new HelpBox("HBLint.Attributes를 읽지 못했습니다 — 콘솔을 보세요.", HelpBoxMessageType.Error));
                return;
            }

            if (!File.Exists(_rulesPath))
            {
                rootVisualElement.Add(new HelpBox($"{_rulesPath}가 없습니다. 설치하면 예시 규칙과 규칙 asmdef를 만듭니다.", HelpBoxMessageType.Info));
                rootVisualElement.Add(new Button(() => { LintInstaller.Install(); Reload(); }) { text = "설치" });
                return;
            }

            var diskText = File.ReadAllText(_rulesPath);
            var restoring = _pendingJson.Length > 0;
            _document = RulesDocument.Load(restoring ? _pendingJson : diskText, _catalog, out var loadErrors);
            if (restoring && _document != null)
                hasUnsavedChanges = true;
            else
                _loadedText = diskText;
            if (_document == null)
            {
                rootVisualElement.Add(new HelpBox($"{_rulesPath}를 편집 창으로 열 수 없습니다. 아래 오류를 텍스트로 고친 뒤 다시 읽으세요.", HelpBoxMessageType.Error));
                foreach (var e in loadErrors)
                    rootVisualElement.Add(new Label(e.ToString()));
                var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row } };
                buttons.Add(new Button(OpenAsText) { text = "텍스트로 열기" });
                buttons.Add(new Button(Reload) { text = "다시 읽기" });
                rootVisualElement.Add(buttons);
                return;
            }

            _document.Rules.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            BuildLayout();
            BuildDetail();
            Revalidate();
            UpdateStatus();
        }

        private void BuildLayout()
        {
            var toolbar = new Toolbar();
            var add = new ToolbarMenu { text = "규칙 더하기" };
            foreach (var kind in _catalog!.Kinds)
                add.menu.AppendAction(kind.Name, _ => AddRule(kind));
            toolbar.Add(add);
            _duplicateButton = new ToolbarButton(Duplicate) { text = "복제" };
            _deleteButton = new ToolbarButton(Delete) { text = "지우기" };
            toolbar.Add(_duplicateButton);
            toolbar.Add(_deleteButton);
            toolbar.Add(new ToolbarSpacer { style = { flexGrow = 1 } });
            toolbar.Add(new ToolbarButton(AskReload) { text = "다시 읽기" });
            toolbar.Add(new ToolbarButton(OpenAsText) { text = "텍스트로 열기" });
            toolbar.Add(new ToolbarButton(SaveChanges) { text = "저장" });
            rootVisualElement.Add(toolbar);

            _status = new Label { style = { paddingLeft = 4, paddingTop = 2, paddingBottom = 2 } };
            rootVisualElement.Add(_status);

            var split = new TwoPaneSplitView(0, 340, TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
            _list = new MultiColumnListView
            {
                itemsSource = _document!.Rules,
                selectionType = SelectionType.Single,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                showBorder = true,
            };
            _list.columns.Add(TextColumn("id", "id", 120, r => r.Id));
            _list.columns.Add(TextColumn("kind", "kind", 110, r => r.Kind));
            _list.columns.Add(TextColumn("severity", "심각도", 70, r => r.Values.TryGetValue("severity", out var v) && v is JsonString s ? s.Value : ""));
            _list.columns.Add(TextColumn("description", "설명", 200, r => r.Description ?? ""));
            _list.selectionChanged += items =>
            {
                _selected = items.OfType<RuleEntry>().FirstOrDefault();
                BuildDetail();
            };
            split.Add(_list);
            _detail = new ScrollView { style = { paddingLeft = 6, paddingRight = 6 } };
            split.Add(_detail);
            rootVisualElement.Add(split);

            rootVisualElement.Add(new Label("검증") { style = { unityFontStyleAndWeight = FontStyle.Bold, paddingLeft = 4, paddingTop = 4 } });
            _errors = new ScrollView { style = { height = 90, paddingLeft = 4 } };
            rootVisualElement.Add(_errors);
        }

        private Column TextColumn(string name, string title, float width, Func<RuleEntry, string> text)
        {
            return new Column
            {
                name = name,
                title = title,
                width = width,
                stretchable = name == "description",
                makeCell = () => new Label(),
                bindCell = (element, index) => ((Label)element).text = text(_document!.Rules[index]),
            };
        }

        private void BuildDetail()
        {
            if (_detail == null)
                return;
            _detail.Clear();
            _duplicateButton?.SetEnabled(_selected != null);
            _deleteButton?.SetEnabled(_selected != null);
            var entry = _selected;
            if (entry == null)
            {
                _detail.Add(new Label("왼쪽에서 규칙을 고르세요."));
                return;
            }
            var kind = _catalog!.Find(entry.Kind)!;

            var id = new TextField("id") { value = entry.Id, isDelayed = true };
            id.RegisterValueChangedCallback(e => { entry.Id = e.newValue; Changed(); });
            _detail.Add(id);
            _detail.Add(new TextField("kind") { value = kind.Name, isReadOnly = true });
            var description = new TextField("description") { value = entry.Description ?? "", multiline = true, isDelayed = true };
            description.RegisterValueChangedCallback(e => { entry.Description = string.IsNullOrEmpty(e.newValue) ? null : e.newValue; Changed(); });
            _detail.Add(description);

            foreach (var field in kind.Fields)
                _detail.Add(FieldRow(entry, field));
        }

        private VisualElement FieldRow(RuleEntry entry, RuleField field)
        {
            var row = new VisualElement { style = { marginTop = 4 } };
            var present = entry.Values.TryGetValue(field.JsonName, out var value);
            if (!field.Required)
            {
                var use = new Toggle($"{field.JsonName} 쓰기") { value = present };
                use.RegisterValueChangedCallback(e =>
                {
                    if (e.newValue)
                        entry.Values[field.JsonName] = RulesDocument.EmptyValue(field);
                    else
                        entry.Values.Remove(field.JsonName);
                    Changed();
                    BuildDetail();
                });
                row.Add(use);
                if (!present)
                    return row;
            }

            if (value != null && !Matches(field, value))
                row.Add(new HelpBox($"파일의 '{field.JsonName}' 값({(value is JsonString text ? "\"" + text.Value + "\"" : value.KindName)})이 맞지 않습니다. 고치면 덮어씁니다.", HelpBoxMessageType.Warning));
            row.Add(ValueEditor(entry, field, value));
            return row;
        }

        private VisualElement ValueEditor(RuleEntry entry, RuleField field, JsonValue? value)
        {
            var label = field.JsonName + (field.Required ? " *" : "");
            switch (field.Kind)
            {
                case RuleValueKind.StringArray:
                {
                    var lines = value is JsonArray array ? string.Join("\n", array.Items.OfType<JsonString>().Select(s => s.Value)) : "";
                    var text = new TextField(label + " (한 줄에 하나)") { value = lines, multiline = true, isDelayed = true };
                    text.RegisterValueChangedCallback(e =>
                    {
                        var result = new JsonArray();
                        foreach (var line in e.newValue.Split('\n'))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.Length > 0)
                                result.Add(new JsonString(trimmed));
                        }
                        Set(entry, field, result);
                    });
                    return text;
                }
                case RuleValueKind.Enum:
                {
                    var names = field.EnumNames.ToList();
                    var dropdown = new DropdownField(label) { choices = names };
                    // 파일 값이 목록에 없으면 비워 둔다 — 첫 항목을 보여 주면 고른 것처럼 보여 다시 골라도 바뀌지 않는다
                    if (value is JsonString current && names.Contains(current.Value))
                        dropdown.SetValueWithoutNotify(current.Value);
                    dropdown.RegisterValueChangedCallback(e => Set(entry, field, new JsonString(e.newValue)));
                    return dropdown;
                }
                case RuleValueKind.Bool:
                {
                    var toggle = new Toggle(label) { value = value is JsonBool b && b.Value };
                    toggle.RegisterValueChangedCallback(e => Set(entry, field, new JsonBool(e.newValue)));
                    return toggle;
                }
                case RuleValueKind.Int:
                {
                    var number = value is JsonNumber n && int.TryParse(n.Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                    var integer = new IntegerField(label) { value = number, isDelayed = true };
                    integer.RegisterValueChangedCallback(e => Set(entry, field, new JsonNumber(e.newValue.ToString(CultureInfo.InvariantCulture))));
                    return integer;
                }
                default:
                {
                    var text = new TextField(label) { value = (value as JsonString)?.Value ?? "", multiline = field.JsonName == "message", isDelayed = true };
                    text.RegisterValueChangedCallback(e => Set(entry, field, new JsonString(e.newValue)));
                    return text;
                }
            }
        }

        private static bool Matches(RuleField field, JsonValue value)
        {
            switch (field.Kind)
            {
                case RuleValueKind.StringArray: return value is JsonArray array && array.Items.All(i => i is JsonString);
                case RuleValueKind.Enum: return value is JsonString name && field.EnumNames.Contains(name.Value);
                case RuleValueKind.Bool: return value is JsonBool;
                case RuleValueKind.Int: return value is JsonNumber;
                default: return value is JsonString;
            }
        }

        private void Set(RuleEntry entry, RuleField field, JsonValue value)
        {
            entry.Values[field.JsonName] = value;
            Changed();
        }

        private void Changed()
        {
            hasUnsavedChanges = true;
            _pendingJson = _document!.ToJson(_catalog!);
            _list?.RefreshItems();
            Revalidate();
            UpdateStatus();
        }

        /// <summary>생성기 검증 + 생성 단계 타입 확인 — 저장 뒤 생성에서 막힐 것을 창에서 미리 보인다.</summary>
        private IReadOnlyList<RulesError> ValidationErrors()
        {
            var json = _document!.ToJson(_catalog!);
            var result = RulesJson.Generate(json, _catalog!);
            return result.Success ? LintGenerator.CheckTypeNames(json, _catalog!) : result.Errors;
        }

        private void Revalidate()
        {
            if (_errors == null || _document == null || _catalog == null)
                return;
            _errors.Clear();
            var errors = ValidationErrors();
            if (errors.Count == 0)
            {
                _errors.Add(new Label("오류 없음"));
                return;
            }
            // 위치는 저장할 글 기준이라 보이지 않고, 규칙 id를 누르면 그 규칙을 고른다
            foreach (var error in errors)
            {
                var label = new Label((error.RuleId == null ? "" : $"[{error.RuleId}] ") + error.Message) { style = { color = new Color(0.95f, 0.45f, 0.4f) } };
                var id = error.RuleId;
                if (id != null)
                    label.RegisterCallback<ClickEvent>(_ => SelectById(id));
                _errors.Add(label);
            }
        }

        private void UpdateStatus()
        {
            if (_status != null)
                _status.text = _rulesPath + (hasUnsavedChanges ? "  — 저장 안 함" : "");
        }

        private void SelectById(string id)
        {
            var index = _document!.Rules.FindIndex(r => r.Id == id);
            if (index >= 0)
                _list?.SetSelection(index);
        }

        private void AddRule(RuleKind kind)
        {
            var entry = RulesDocument.NewRule(UniqueId("new-rule"), kind);
            _document!.Rules.Add(entry);
            _list!.RefreshItems();
            _list.SetSelection(_document.Rules.Count - 1);
            Changed();
        }

        private void Duplicate()
        {
            if (_selected == null)
                return;
            var copy = new RuleEntry(UniqueId(_selected.Id + "-copy"), _selected.Kind) { Description = _selected.Description };
            // 값은 고칠 때 통째로 바꿔 끼우므로(제자리 수정 없음) 같은 JsonValue를 함께 써도 된다
            foreach (var pair in _selected.Values)
                copy.Values[pair.Key] = pair.Value;
            _document!.Rules.Add(copy);
            _list!.RefreshItems();
            _list.SetSelection(_document.Rules.Count - 1);
            Changed();
        }

        private void Delete()
        {
            if (_selected == null || !EditorUtility.DisplayDialog("HB Lint", $"규칙 '{_selected.Id}'를 지울까요? (저장해야 파일에 반영)", "지우기", "취소"))
                return;
            _document!.Rules.Remove(_selected);
            _selected = null;
            _list!.ClearSelection();
            _list.RefreshItems();
            BuildDetail();
            Changed();
        }

        private string UniqueId(string baseId)
        {
            var ids = new HashSet<string>(_document!.Rules.Select(r => r.Id), StringComparer.Ordinal);
            if (!ids.Contains(baseId))
                return baseId;
            for (var i = 2; ; i++)
            {
                var candidate = baseId + "-" + i.ToString(CultureInfo.InvariantCulture);
                if (!ids.Contains(candidate))
                    return candidate;
            }
        }

        private void AskReload()
        {
            if (hasUnsavedChanges && !EditorUtility.DisplayDialog("HB Lint", "저장하지 않은 변경을 버리고 다시 읽을까요?", "다시 읽기", "취소"))
                return;
            _pendingJson = "";
            Reload();
        }

        private void OpenAsText() => InternalEditorUtility.OpenFileAtLineExternal(Path.GetFullPath(_rulesPath), 1);
    }
}
