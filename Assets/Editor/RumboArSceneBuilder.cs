using Rumbo.Ar.Application;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.ARCore;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;
using UnityEngine.XR.ARCore;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;

namespace Rumbo.Editor
{
    public static class RumboArSceneBuilder
    {
        const string ScenePath = "Assets/Scenes/RumboArPrototype.unity";

        [MenuItem("Rumbo/Build AR prototype scene")]
        public static void Build()
        {
            System.IO.Directory.CreateDirectory("Assets/Scenes");
            System.IO.Directory.CreateDirectory("Assets/Materials");
            System.IO.Directory.CreateDirectory("Assets/Prefabs");

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new System.Exception("URP Unlit shader missing.");
            var planeMaterial = MaterialAsset("Assets/Materials/DetectedPlane.mat", shader,
                new Color(0.08f, 0.65f, 0.9f, 0.28f));
            planeMaterial.SetFloat("_Surface", 1f);
            planeMaterial.SetFloat("_Blend", 0f);
            planeMaterial.renderQueue = 3000;
            var arrowMaterial = MaterialAsset("Assets/Materials/DemoArrow.mat", shader,
                new Color(1f, 0.72f, 0.05f, 1f));

            var planePrefab = new GameObject("Tracked horizontal plane");
            planePrefab.AddComponent<MeshFilter>();
            planePrefab.AddComponent<MeshRenderer>().sharedMaterial = planeMaterial;
            planePrefab.AddComponent<ARPlaneMeshVisualizer>();
            var savedPlanePrefab = PrefabUtility.SaveAsPrefabAsset(planePrefab, "Assets/Prefabs/TrackedPlane.prefab");
            Object.DestroyImmediate(planePrefab);

            var arrowPrefab = new GameObject("DEMO arrow - not a route");
            ArrowPart(arrowPrefab.transform, "shaft", new Vector3(0, 0, 0),
                new Vector3(.055f, .025f, .27f), 0, arrowMaterial);
            ArrowPart(arrowPrefab.transform, "left wing", new Vector3(-.065f, 0, .15f),
                new Vector3(.045f, .025f, .16f), -45, arrowMaterial);
            ArrowPart(arrowPrefab.transform, "right wing", new Vector3(.065f, 0, .15f),
                new Vector3(.045f, .025f, .16f), 45, arrowMaterial);
            var savedArrowPrefab = PrefabUtility.SaveAsPrefabAsset(arrowPrefab, "Assets/Prefabs/DemoArrow.prefab");
            Object.DestroyImmediate(arrowPrefab);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var session = new GameObject("AR Session", typeof(ARSession), typeof(ARInputManager)).GetComponent<ARSession>();
            session.enabled = false;
            var originObject = new GameObject("XR Origin", typeof(XROrigin),
                typeof(ARPlaneManager), typeof(ARRaycastManager), typeof(ARAnchorManager));
            var origin = originObject.GetComponent<XROrigin>();
            var cameraOffset = new GameObject("Camera Offset");
            cameraOffset.transform.SetParent(originObject.transform, false);
            var cameraObject = new GameObject("AR Camera", typeof(Camera), typeof(AudioListener),
                typeof(ARCameraManager), typeof(ARCameraBackground), typeof(TrackedPoseDriver));
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(cameraOffset.transform, false);
            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Color;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = .1f;
            camera.farClipPlane = 20f;
            origin.CameraFloorOffsetObject = cameraOffset;
            origin.Camera = camera;
            var poseDriver = cameraObject.GetComponent<TrackedPoseDriver>();
            var position = new InputAction("Position", binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3");
            position.AddBinding("<HandheldARInputDevice>/devicePosition");
            var rotation = new InputAction("Rotation", binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion");
            rotation.AddBinding("<HandheldARInputDevice>/deviceRotation");
            poseDriver.positionInput = new InputActionProperty(position);
            poseDriver.rotationInput = new InputActionProperty(rotation);

            var planes = originObject.GetComponent<ARPlaneManager>();
            planes.requestedDetectionMode = PlaneDetectionMode.Horizontal;
            planes.planePrefab = savedPlanePrefab;
            var controllerObject = new GameObject("RumboArController");
            var controller = controllerObject.AddComponent<RumboArController>();
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("planeManager").objectReferenceValue = planes;
            serialized.FindProperty("session").objectReferenceValue = session;
            serialized.FindProperty("raycastManager").objectReferenceValue = originObject.GetComponent<ARRaycastManager>();
            serialized.FindProperty("anchorManager").objectReferenceValue = originObject.GetComponent<ARAnchorManager>();
            serialized.FindProperty("arCamera").objectReferenceValue = camera;
            serialized.FindProperty("arrowPrefab").objectReferenceValue = savedArrowPrefab;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var canvasObject = new GameObject("Prototype controls", typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasObject.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasObject.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1080, 1920);
            var eventSystem = new GameObject("Event System", typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
            Button(canvasObject.transform, "Left -15", new Vector2(-310, 120), controller.TurnLeft);
            Button(canvasObject.transform, "Right +15", new Vector2(310, 120), controller.TurnRight);
            Button(canvasObject.transform, "Exit to Flutter", new Vector2(0, 120), controller.Exit);
            Label(canvasObject.transform, "DEMO · Tap a detected floor or table to place arrow", new Vector2(0, -120));

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            ConfigureAndroid();
            AssetDatabase.SaveAssets();
            Debug.Log("Rumbo AR scene and Android XR settings created.");
        }

        static Material MaterialAsset(string path, Shader shader, Color color)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
            material.SetColor("_BaseColor", color);
            EditorUtility.SetDirty(material);
            return material;
        }

        static void ArrowPart(Transform parent, string name, Vector3 position, Vector3 scale,
            float yaw, Material material)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            part.transform.localRotation = Quaternion.Euler(0, yaw, 0);
            part.GetComponent<Renderer>().sharedMaterial = material;
            Object.DestroyImmediate(part.GetComponent<Collider>());
        }

        static void Button(Transform parent, string title, Vector2 position, UnityEngine.Events.UnityAction action)
        {
            var root = new GameObject(title, typeof(RectTransform), typeof(Image), typeof(UnityEngine.UI.Button));
            root.transform.SetParent(parent, false);
            var rect = root.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, 0);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(270, 88);
            root.GetComponent<Image>().color = new Color(.05f, .12f, .18f, .9f);
            UnityEventTools.AddPersistentListener(root.GetComponent<UnityEngine.UI.Button>().onClick, action);
            Label(root.transform, title, Vector2.zero);
        }

        static void Label(Transform parent, string value, Vector2 position)
        {
            var root = new GameObject(value, typeof(RectTransform), typeof(Text));
            root.transform.SetParent(parent, false);
            var rect = root.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, parent.GetComponent<Canvas>() != null ? 1f : .5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(950, 100);
            var text = root.GetComponent<Text>();
            text.text = value;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 27;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
        }

        static void ConfigureAndroid()
        {
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.uitopic.rumbo.ar");
            var settings = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>("Assets/Settings/RumboXRSettings.asset");
            if (settings == null)
            {
                System.IO.Directory.CreateDirectory("Assets/Settings");
                settings = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(settings, "Assets/Settings/RumboXRSettings.asset");
            }
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, settings, true);
            if (!settings.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
                settings.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            var manager = settings.ManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            manager.automaticLoading = true;
            manager.automaticRunning = true;
            EditorUtility.SetDirty(manager);
            if (!XRPackageMetadataStore.AssignLoader(manager,
                typeof(ARCoreLoader).FullName, BuildTargetGroup.Android))
                throw new System.Exception("ARCore loader assignment failed.");
            ARCoreSettings.GetOrCreateSettings().requirement = ARCoreSettings.Requirement.Optional;
        }

        [MenuItem("Rumbo/Export Android library")]
        public static void ExportAndroid()
        {
            if (!System.IO.File.Exists(ScenePath)) Build();
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
                throw new System.InvalidOperationException(
                    "Install Unity Android Build Support (SDK, NDK and OpenJDK) for this Editor version.");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
                throw new System.InvalidOperationException("Could not activate the Android build target.");
            System.IO.Directory.CreateDirectory("Builds/Android");
            // Some Android drivers fail to import the ARCore camera buffer under Vulkan.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
            EditorUserBuildSettings.exportAsGoogleAndroidProject = true;
            var options = new BuildPlayerOptions {
                scenes = new[] { ScenePath },
                locationPathName = "Builds/Android",
                target = BuildTarget.Android,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                throw new System.Exception("Unity Android export failed: " + report.summary.result);
        }
    }
}
