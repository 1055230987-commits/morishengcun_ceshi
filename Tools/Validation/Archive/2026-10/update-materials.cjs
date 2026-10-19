const fs = require('fs');
function edit(p, fn) {
    const s = fs.readFileSync(p, 'utf8').replace(/\r\n/g, '\n');
    fs.writeFileSync(p, fn(s).replace(/\n/g, '\r\n'));
}
function between(s, a, b, replacement) {
    const start = s.indexOf(a), end = s.indexOf(b, start);
    if (start < 0 || end < 0) throw Error(a);
    return s.slice(0, start) + replacement + s.slice(end);
}
edit('Assets/_Project/Gameplay/Building/BuildingFloor.cs', s => {
    s = s.replace('using UnityEngine;', 'using UnityEngine;\nusing Game.Rendering;');
    s = s.replace('        private Material[] _materials;\n        private Color[] _baseColors;\n        private bool _materialsCached;',
        '        private RendererFadeMaterials _fadeMaterials;');
    s = s.replace('            _contentsDirty = false;', '            ReleaseMaterials();\n            _contentsDirty = false;');
    s = s.replace(/ +_materialsCached = false;\n +_materials = null;\n +_baseColors = null;\n/g, '');
    const code = [
        '        private void ApplyGhostAlpha()',
        '        {',
        '            if (_fadeMaterials == null) _fadeMaterials = new RendererFadeMaterials(_renderers);',
        '            _fadeMaterials.Apply(_ghostAlpha);',
        '        }',
        '',
        '        private void RestoreOpaque() => _fadeMaterials?.Restore();',
        '',
        '        private void ReleaseMaterials()',
        '        {',
        '            _fadeMaterials?.Dispose();',
        '            _fadeMaterials = null;',
        '        }',
        '',
        '        private void OnDestroy() => ReleaseMaterials();',
        '',
    ].join('\n');
    s = between(s, '        /// <summary>\n        /// 缓存材质实例和原色。', '#if UNITY_EDITOR', code);
    s = s.replace('完全隐藏：渲染关闭、碰撞关闭、交互关闭。','完全隐藏：渲染与交互关闭，碰撞保留。')
         .replace('半透明显示：能看到轮廓但不遮挡视线。碰撞与交互关闭。','半透明显示：能看到轮廓但不遮挡视线。交互关闭，碰撞保留。');
    return s;
});
edit('Assets/_Project/Gameplay/Gathering/InteractableTree.cs', s => {
    s = s.replace('using UnityEngine;', 'using UnityEngine;\nusing Game.Rendering;');
    s = s.replace('        private Material[] _materials;\n        private Color[] _initialColors;', '        private RendererFadeMaterials _fadeMaterials;');
    s = s.replace('if (_materials == null || _materials.Length == 0)', 'if (_fadeMaterials == null)');
    const code = [
        '        private void CacheFadeMaterials()',
        '        {',
        '            if (_fadeMaterials == null && _renderers != null)',
        '                _fadeMaterials = new RendererFadeMaterials(_renderers);',
        '        }',
        '',
        '        private void ApplyAlpha(float alpha) => _fadeMaterials?.Apply(alpha);',
        '',
        '        private void OnDestroy() => _fadeMaterials?.Dispose();',
        '',
    ].join('\n');
    s = between(s, '        /// <summary>取得材质实例并记下初始颜色（只做一次）。</summary>', '        private void HideRenderers()', code);
    s = s.replace('            ApplyAlpha(1f);', '            _fadeMaterials?.Restore();');
    s = s.replace('用"实例化材质"的方式做透明：renderer.materials 会给出该渲染器的材质实例，', '使用显式拥有的材质副本做透明，复位时恢复原材质，销毁时释放副本，');
    return s;
});
edit('Assets/_Project/World/Environment/DayNightPostProcessing.cs', s => {
    s = s.replace('[ExecuteAlways]', '[ExecuteAlways]\n    [DefaultExecutionOrder(110)]');
    s = s.replace('        private bool _initialized;', '        private bool _initialized;\n        private VolumeProfile _instanceProfile;\n        private VolumeProfile _previousInstance;\n        private Volume _instanceVolume;');
    s = s.replace('public VolumeProfile Profile => _profile;', 'public VolumeProfile Profile => _profile;');
    s = s.replace('_initialized = _volume != null && _profile != null;', '_initialized = _volume != null && _instanceProfile != null;');
    const a = s.indexOf('            if (_profile == null)\n', s.indexOf('        private void EnsureVolume()'));
    const b = s.indexOf('        private void EnsureComponents()', a);
    if (a < 0 || b < 0) throw Error('EnsureVolume');
    s = s.slice(0, a) + [
        '            if (_instanceProfile != null) return;',
        '            VolumeProfile source = _profile != null ? _profile : _volume.sharedProfile;',
        '            _previousInstance = _volume.HasInstantiatedProfile() ? _volume.profile : null;',
        '            _instanceVolume = _volume;',
        '            _instanceProfile = ScriptableObject.CreateInstance<VolumeProfile>();',
        '            _instanceProfile.name = "DayNightProfile_Instance";',
        '            _instanceProfile.hideFlags = HideFlags.HideAndDontSave;',
        '            if (source != null)',
        '            {',
        '                foreach (VolumeComponent component in source.components)',
        '                {',
        '                    if (component == null) continue;',
        '                    VolumeComponent copy = Instantiate(component);',
        '                    copy.hideFlags = HideFlags.HideAndDontSave;',
        '                    _instanceProfile.components.Add(copy);',
        '                }',
        '            }',
        '            _volume.profile = _instanceProfile;',
        '        }',
        '',
        '        private void OnDisable() => ReleaseProfile();',
        '        private void OnDestroy() => ReleaseProfile();',
        '',
        '        private void ReleaseProfile()',
        '        {',
        '            if (_instanceProfile != null)',
        '            {',
        '                if (_instanceVolume != null && _instanceVolume.HasInstantiatedProfile() &&',
        '                    _instanceVolume.profile == _instanceProfile)',
        '                    _instanceVolume.profile = _previousInstance;',
        '                foreach (VolumeComponent component in _instanceProfile.components)',
        '                    CoreUtils.Destroy(component);',
        '                CoreUtils.Destroy(_instanceProfile);',
        '            }',
        '            _instanceProfile = null;',
        '            _instanceVolume = null;',
        '            _previousInstance = null;',
        '            _tonemapping = null;',
        '            _colorAdjustments = null;',
        '            _bloom = null;',
        '            _vignette = null;',
        '            _initialized = false;',
        '        }',
        '',
    ].join('\n') + s.slice(b);
    const start=s.indexOf('        private void EnsureComponents()'), end=s.indexOf('        private void EnableCameraPostProcessing()',start);
    s=s.slice(0,start)+s.slice(start,end).replace(/_profile/g,'_instanceProfile')+s.slice(end);
    s=s.replace('            _profile = profile;\n            _volume = volume;', '            ReleaseProfile();\n            _profile = profile;\n            _volume = volume;');
    return s;
});
