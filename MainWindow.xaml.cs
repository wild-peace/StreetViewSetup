using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using System.Drawing; // 用于 Bitmap
using Point = System.Windows.Point; // 明确指定 Point 类型
using HelixToolkit.Geometry;
using System.Windows.Controls;
using Microsoft.Win32;
using static LocalStreetViewApp.MapManager;

namespace LocalStreetViewApp
{

    /// <summary>
    /// 主窗口交互逻辑
    /// 负责协调 3D 全景视图与 2D 电子地图的联动
    /// </summary>
    public partial class MainWindow : Window
    {
        #region 1. 成员变量与状态

        // --- 3D 视图状态 ---
        private double rotationY = 0.0; // 左右旋转角度
        private double rotationX = 0.0; // 上下旋转角度
        private bool isDragging = false; // 3D视图是否正在拖拽
        private Point lastMousePos;

        // --- 3D 场景对象 ---
        private PerspectiveCamera camera;
        private ModelVisual3D sphereModelVisual; // 球体容器
        private GeometryModel3D sphereGeometry;  // 全景球体模型
        private GeometryModel3D compassGeometry; // 脚下指南针模型
        private Point dragStartPoint; // 记录按下时的位置
        private bool isClickOperation = false; // 标记是否是一次点击操作

        // --- 2D 地图状态 ---
        private bool isMapPanning = false; // 地图是否正在平移
        private Point mapPanStartPoint;
        private MapManager mapManager;
        private CoordinateTransformer coordinateTransformer; // 坐标转换器 (屏幕<->地理)

        // --- 数据 ---
        private List<StreetNode> streetNodes = new List<StreetNode>();
        private int currentIndex = 0; // 当前查看的节点索引

        #endregion

        #region 2. 初始化 (Initialization)

        public MainWindow()
        {
            InitializeComponent();

            // 初始化 3D 环境
            SetupViewport();

            // 初始化 2D 地图引擎
            InitializeMap();

            // 绑定交互事件
            AttachMouseHandlers();
            this.KeyDown += MainWindow_KeyDown;

            // 监听窗口大小改变以重绘地图
            MapImage.SizeChanged += (s, e) => UpdateMapDisplay();
        }

        private void InitializeMap()
        {
            try
            {
                mapManager = new MapManager();
                UpdateMapDisplay();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"地图初始化失败: {ex.Message}");
            }
        }

        private void SetupViewport()
        {
            // 配置相机
            camera = new PerspectiveCamera
            {
                FieldOfView = 70, // 这里的数值越大，广角越强；越小，畸变越小
                Position = new Point3D(0, 0, 0), // 相机在球心
                LookDirection = new Vector3D(0, 0, 1), // 初始朝向
                UpDirection = new Vector3D(0, 1, 0) // ★关键：强制头顶朝上，防止晕眩
            };
            helixViewport.Camera = camera;

            helixViewport.Children.Add(new DefaultLights());
            sphereModelVisual = new ModelVisual3D();
            helixViewport.Children.Add(sphereModelVisual);
        }

        #endregion

        #region 3. 2D 地图交互逻辑 (Map Interaction)

        /// <summary>
        /// 核心方法：刷新地图显示
        /// 从 MapManager 获取 Bitmap 并转换为 WPF ImageSource
        /// </summary>
        private void UpdateMapDisplay()
        {
            try
            {
                if (mapManager == null) return;

                int w = (int)MapImage.ActualWidth;
                int h = (int)MapImage.ActualHeight;
                if (w <= 0 || h <= 0) { w = 800; h = 300; }

                // 获取当前高亮的点 ID
                int currentId = -1;
                if (streetNodes.Count > 0 && currentIndex >= 0 && currentIndex < streetNodes.Count)
                {
                    currentId = streetNodes[currentIndex].Id;
                }

                // 调用 MapManager 渲染 (GDI+)
                using (var mapBitmap = mapManager.RenderMap(streetNodes, w, h, currentId))
                {
                    MapImage.Source = BitmapToImageSource(mapBitmap);
                }

                // 更新坐标转换器 (用于鼠标点击判定)
                var currentBounds = mapManager.GetCurrentViewBounds();
                coordinateTransformer = new CoordinateTransformer(currentBounds, new System.Windows.Size(w, h));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"更新地图显示失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 地图左键点击：跳转到最近的全景点
        /// </summary>
        private void MapImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (streetNodes.Count == 0 || coordinateTransformer == null) return;

            // 1. 获取点击的地理坐标
            var clickPoint = e.GetPosition(MapImage);
            var (geoX, geoY) = coordinateTransformer.ScreenToGeo(clickPoint);

            // 2. 查找地理上最近的节点
            var candidateNode = FindNearestNodeWithImage(geoX, geoY);

            if (candidateNode != null)
            {
                // ★★★ 核心校验：防止误触 ★★★
                // 将节点的地理坐标转回屏幕像素坐标，计算点击偏差
                var nodeScreenPos = coordinateTransformer.GeoToScreen(candidateNode.Lon, candidateNode.Lat);

                double dx = clickPoint.X - nodeScreenPos.X;
                double dy = clickPoint.Y - nodeScreenPos.Y;
                double pixelDistance = Math.Sqrt(dx * dx + dy * dy);

                // 阈值设为 15 像素 (大约手指触摸的大小)
                double hitThreshold = 15.0;

                if (pixelDistance <= hitThreshold)
                {
                    int newIndex = streetNodes.IndexOf(candidateNode);
                    if (newIndex != currentIndex)
                    {
                        currentIndex = newIndex;
                        LoadPanorama(currentIndex);
                    }
                }
            }
        }

        /// <summary>
        /// 地图右键按下：开始平移
        /// </summary>
        private void MapImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            isMapPanning = true;
            mapPanStartPoint = e.GetPosition(MapImage);

            // 捕获鼠标，防止拖拽出图片范围后状态丢失
            MapImage.CaptureMouse();
            MapImage.Cursor = Cursors.SizeAll;
        }

        /// <summary>
        /// 地图鼠标移动：处理平移逻辑
        /// </summary>
        private void MapImage_MouseMove(object sender, MouseEventArgs e)
        {
            // 1. 处理平移
            if (isMapPanning && mapManager != null)
            {
                var currentPoint = e.GetPosition(MapImage);
                double dx = currentPoint.X - mapPanStartPoint.X;
                double dy = currentPoint.Y - mapPanStartPoint.Y;

                // 防抖动阈值
                if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1)
                {
                    mapManager.Pan(dx, dy, MapImage.ActualWidth, MapImage.ActualHeight);
                    UpdateMapDisplay();
                    mapPanStartPoint = currentPoint;
                }
            }

            // 2. 显示鼠标下的地理坐标 (调试用)
            if (coordinateTransformer != null)
            {
                var mousePoint = e.GetPosition(MapImage);
                var (x, y) = coordinateTransformer.ScreenToGeo(mousePoint);
                MapCoordText.Text = $"坐标: ({x:F6}, {y:F6})";
            }
        }

        /// <summary>
        /// 地图右键抬起：结束平移
        /// </summary>
        private void MapImage_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (isMapPanning)
            {
                isMapPanning = false;
                MapImage.ReleaseMouseCapture();
                MapImage.Cursor = Cursors.Arrow;
            }
        }

        /// <summary>
        /// 地图滚轮：以鼠标为中心缩放
        /// </summary>
        private void MapImage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (mapManager == null) return;
            var mousePos = e.GetPosition(MapImage);
            mapManager.ZoomAtPoint(e.Delta, mousePos.X, mousePos.Y, MapImage.ActualWidth, MapImage.ActualHeight);
            UpdateMapDisplay();
            e.Handled = true; // 阻止事件冒泡
        }

        #endregion

        #region 4. 3D 全景逻辑 (Panorama Logic)

        /// <summary>
        /// 加载指定索引的全景图
        /// </summary>
        private void LoadPanorama(int index)
        {
            if (streetNodes.Count == 0 || index < 0 || index >= streetNodes.Count) return;

            var node = streetNodes[index];
            if (string.IsNullOrEmpty(node.ImagePath) || !File.Exists(node.ImagePath))
            {
                MessageBox.Show($"图片不存在: {node.ImagePath}");
                return;
            }

            currentIndex = index;

            try
            {
                // 1. 读取并冻结图片资源 (高性能加载)
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(node.ImagePath, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                // 2. 创建自发光材质 (用于全景展示)
                var imgBrush = new ImageBrush(bmp)
                {
                    Stretch = Stretch.Fill,
                    ViewportUnits = BrushMappingMode.Absolute
                };
                RenderOptions.SetBitmapScalingMode(imgBrush, BitmapScalingMode.HighQuality);
                var material = new EmissiveMaterial(imgBrush);

                // 3. 创建或更新球体几何
                var mesh = CreateSphereMesh(10, 64, 32);

                if (sphereGeometry == null)
                {
                    sphereGeometry = new GeometryModel3D();
                }

                // 更新几何体和材质
                sphereGeometry.Geometry = mesh;
                sphereGeometry.Material = material;
                sphereGeometry.BackMaterial = material; // 确保内部可见

                // 4. 组装场景 (球体 + 指南针)
                var group = new Model3DGroup();
                group.Children.Add(sphereGeometry);

                // 重新创建指南针并加入场景
                CreateCompass();
                if (compassGeometry != null) group.Children.Add(compassGeometry);

                sphereModelVisual.Content = group;

                // 5. 应用旋转并更新 UI
                UpdateCameraDirection();

                string fileName = Path.GetFileName(node.ImagePath);
                txtInfo.Text = $"{currentIndex + 1}/{streetNodes.Count} - {fileName}";

                // 同步刷新 2D 地图的高亮位置
                UpdateMapDisplay();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载全景失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建 3D 脚下指南针
        /// </summary>
        private void CreateCompass()
        {
            var meshBuilder = new MeshBuilder();
            double size = 8.0;
            double yPos = -5.0; // 放置在脚下

            // 创建 XZ 平面上的四边形
            meshBuilder.AddQuad(
               new System.Numerics.Vector3((float)-size, (float)yPos, (float)size),
               new System.Numerics.Vector3((float)size, (float)yPos, (float)size),
               new System.Numerics.Vector3((float)size, (float)yPos, (float)-size),
               new System.Numerics.Vector3((float)-size, (float)yPos, (float)-size)
            );

            // 获取 XAML 中的 Visual 资源作为材质
            var compassGrid = (Grid)this.FindResource("CompassDesign");
            var visualBrush = new VisualBrush(compassGrid);

            // 关键优化：缓存 VisualBrush 渲染，防止性能下降
            RenderOptions.SetCachingHint(visualBrush, CachingHint.Cache);

            var material = new DiffuseMaterial(visualBrush);
            compassGeometry = new GeometryModel3D(meshBuilder.ToMesh().ToWndMeshGeometry3D(), material)
            {
                BackMaterial = material // 确保背面（从下面看）也可见
            };
        }

        /// <summary>
        /// 手动创建球体网格 (确保 UV 纹理坐标正确映射全景图)
        /// </summary>
        private System.Windows.Media.Media3D.MeshGeometry3D CreateSphereMesh(double radius, int thetaDiv, int phiDiv)
        {
            var mesh = new System.Windows.Media.Media3D.MeshGeometry3D();
            double dt = Math.PI / thetaDiv;
            double dp = 2 * Math.PI / phiDiv;

            // 生成顶点与 UV
            for (int pi = 0; pi <= thetaDiv; pi++)
            {
                double theta = pi * dt;
                for (int ti = 0; ti <= phiDiv; ti++)
                {
                    double phi = ti * dp;

                    double x = radius * Math.Sin(theta) * Math.Cos(phi);
                    double y = radius * Math.Cos(theta);
                    double z = radius * Math.Sin(theta) * Math.Sin(phi);

                    mesh.Positions.Add(new Point3D(x, y, z));
                    mesh.TextureCoordinates.Add(new Point(ti / (double)phiDiv, pi / (double)thetaDiv));
                    mesh.Normals.Add(new Vector3D(x, y, z));
                }
            }

            // 生成三角形索引
            for (int pi = 0; pi < thetaDiv; pi++)
            {
                for (int ti = 0; ti < phiDiv; ti++)
                {
                    int x0 = ti;
                    int x1 = (ti + 1);
                    int y0 = pi * (phiDiv + 1);
                    int y1 = (pi + 1) * (phiDiv + 1);

                    mesh.TriangleIndices.Add(y0 + x0);
                    mesh.TriangleIndices.Add(y1 + x0);
                    mesh.TriangleIndices.Add(y0 + x1);

                    mesh.TriangleIndices.Add(y1 + x0);
                    mesh.TriangleIndices.Add(y1 + x1);
                    mesh.TriangleIndices.Add(y0 + x1);
                }
            }
            return mesh;
        }

        /// <summary>
        /// 更新 3D 场景的旋转 (球体 + 指南针)
        /// </summary>
        private void UpdateCameraDirection()
        {
            // 将角度转换为弧度
            double yRad = rotationY * Math.PI / 180.0;
            double xRad = rotationX * Math.PI / 180.0;

            double lookX = Math.Sin(yRad) * Math.Cos(xRad);
            double lookY = Math.Sin(xRad);
            double lookZ = -Math.Cos(yRad) * Math.Cos(xRad); // 负号是为了匹配通常的 Z 轴深度

            camera.LookDirection = new Vector3D(lookX, lookY, lookZ);

            camera.UpDirection = new Vector3D(0, 1, 0);

        }
        private void UpdateSphereRotation()
        {
            var transformGroup = new Transform3DGroup();

            // X轴旋转 (俯仰)
            transformGroup.Children.Add(new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(1, 0, 0), rotationX)));

            // Y轴旋转 (水平)
            transformGroup.Children.Add(new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(0, 1, 0), rotationY)));

            // 同时应用给全景球和指南针
            if (sphereGeometry != null) sphereGeometry.Transform = transformGroup;
            if (compassGeometry != null) compassGeometry.Transform = transformGroup;
        }

        #endregion

        #region 5. 3D 视图交互 (Viewport Interaction)

        private void HelixViewport_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                isDragging = true;
                lastMousePos = e.GetPosition(this);

                // --- 新增: 记录初始点击位置用于判定是否为点击 ---
                dragStartPoint = e.GetPosition(helixViewport);
                isClickOperation = true;
                // ---------------------------------------------

                helixViewport.CaptureMouse();
                Mouse.OverrideCursor = Cursors.SizeAll;
            }
        }
        /// <summary>
        /// 绑定 3D 视口的鼠标交互事件
        /// </summary>
        private void AttachMouseHandlers()
        {
            // 确保 helixViewport 在 XAML 中已定义且名称正确
            helixViewport.MouseDown += HelixViewport_MouseDown;
            helixViewport.MouseMove += HelixViewport_MouseMove;
            helixViewport.MouseUp += HelixViewport_MouseUp;
            helixViewport.MouseWheel += HelixViewport_MouseWheel;
        }
        private void HelixViewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging) return;
            var pos = e.GetPosition(this);
            double dx = pos.X - lastMousePos.X;
            double dy = pos.Y - lastMousePos.Y;
            lastMousePos = pos;

            // --- 新增: 判定是否取消点击状态 ---
            // 如果累计移动超过 5 像素，则认为是查看视角，而不是点击跳转
            var currentViewportPos = e.GetPosition(helixViewport);
            if (Math.Abs(currentViewportPos.X - dragStartPoint.X) > 5 ||
                Math.Abs(currentViewportPos.Y - dragStartPoint.Y) > 5)
            {
                isClickOperation = false;
            }
            // --------------------------------

            // 原有的旋转逻辑...
            double rotationSpeed = 0.15;
            rotationY = (rotationY - dx * rotationSpeed) % 360;
            rotationX = rotationX + dy * rotationSpeed;
            rotationX = Math.Max(-89, Math.Min(89, rotationX));
            UpdateCameraDirection();
        }

        private void HelixViewport_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                // --- 新增: 处理点击跳转逻辑 ---
                if (isClickOperation && streetNodes.Count > 0)
                {
                    Handle3DViewClick(e.GetPosition(helixViewport));
                }
                // -----------------------------

                isDragging = false;
                helixViewport.ReleaseMouseCapture();
                Mouse.OverrideCursor = null;
            }
        }

        /// <summary>
        /// 处理 3D 视图的点击事件
        /// </summary>
        private void Handle3DViewClick(Point mousePos)
        {
            // 1. 直接获取最近的点击点 (返回的是 Point3D? 类型)
            // FindNearestPoint 是 HelixToolkit.Wpf 提供的扩展方法
            var hitPointNullable = helixViewport.Viewport.FindNearestPoint(mousePos);

            if (hitPointNullable.HasValue)
            {
                var hitPoint = hitPointNullable.Value;

                // 2. 将 3D 向量转换为方位角 (0-360度)
                // 根据之前的相机逻辑：Z轴负方向为北(0度)，X轴正方向为东(90度)
                double angle = Math.Atan2(hitPoint.X, -hitPoint.Z) * 180.0 / Math.PI;

                // 规范化到 0-360
                if (angle < 0) angle += 360;

                // 3. 在该角度寻找最近的下一张图
                if (mapManager != null && streetNodes.Count > currentIndex)
                {
                    // 容差设为 30 度，最大距离 100 米
                    var nextNode = mapManager.FindNextNodeInDirection(
                        streetNodes[currentIndex].Id,
                        angle,
                        maxDistance: 100,
                        angleTolerance: 30
                    );

                    if (nextNode != null)
                    {
                        int nextIndex = streetNodes.IndexOf(nextNode);
                        if (nextIndex >= 0)
                        {
                            LoadPanorama(nextIndex);
                            // 调试输出
                            // Console.WriteLine($"点击方向: {angle:F0}°, 跳转到: {nextNode.Id}");
                        }
                    }
                }
            }
        }
        private void HelixViewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 通过调整 FOV 来模拟缩放
            double delta = e.Delta > 0 ? -2 : 2;
            camera.FieldOfView = Math.Max(20, Math.Min(100, camera.FieldOfView + delta));
        }

        #endregion

        #region 6. UI 事件处理器 (UI Handlers)

        // 新增：点击按钮手动加载图片
        private void BtnLoadImages_Click(object sender, RoutedEventArgs e)
        {
            // 1. 前置检查
            if (mapManager == null || streetNodes.Count == 0)
            {
                MessageBox.Show("请先加载 SHP 节点数据！");
                return;
            }

            try
            {
                // 2. 选择文件夹 (通过选择文件夹内的一张图片来获取路径)
                OpenFileDialog dlg = new OpenFileDialog
                {
                    Filter = "街景图片 (*.jpg;*.png)|*.jpg;*.png",
                    Title = "请选择图片文件夹（选中该文件夹内任意一张图片即可）",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (dlg.ShowDialog() == true)
                {
                    // 获取文件所在的目录
                    string imageFolder = Path.GetDirectoryName(dlg.FileName);

                    // 3. 执行绑定
                    int boundCount = mapManager.AutoBindImages(imageFolder);

                    // 4. 刷新地图显示（更新红点/灰点状态）
                    UpdateMapDisplay();

                    string message = $"图片绑定完成\n路径: {imageFolder}\n成功绑定: {boundCount} 张";

                    // 5. 自动跳转到第一个有图的节点
                    if (boundCount > 0)
                    {
                        // 查找第一个有图的节点
                        var firstNode = streetNodes.FirstOrDefault(n => !string.IsNullOrEmpty(n.ImagePath));
                        if (firstNode != null)
                        {
                            currentIndex = streetNodes.IndexOf(firstNode);
                            LoadPanorama(currentIndex);
                        }
                    }
                    else
                    {
                        message += "\n(未找到与坐标匹配的文件名，请检查文件名格式是否为 经度_纬度.jpg)";
                    }

                    MessageBox.Show(message);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载图片失败: {ex.Message}");
            }
        }

        // 修改：加载点 SHP (移除了自动绑定图片的逻辑)
        private void BtnLoadSHP_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenFileDialog dlg = new OpenFileDialog
                {
                    Filter = "Shapefile (*.shp)|*.shp",
                    Title = "选择 SHP 数据文件",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                };

                if (dlg.ShowDialog() == true)
                {
                    // 1. 加载 SHP
                    mapManager.LoadShapefile(dlg.FileName);
                    streetNodes = mapManager.Nodes;

                    // 2. 仅刷新地图，不再自动绑定图片
                    UpdateMapDisplay();

                    // 3. 初始化坐标转换器
                    var mapBounds = mapManager.GetMapBounds();
                    coordinateTransformer = new CoordinateTransformer(mapBounds,
                        new System.Windows.Size(MapImage.ActualWidth, MapImage.ActualHeight));

                    MessageBox.Show($"SHP 读取完毕，共 {streetNodes.Count} 个点。\n请点击“加载图片”按钮选择图片文件夹。");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载SHP失败: {ex.Message}");
            }
        }

        // 加载路网 SHP (保持不变)
        private void BtnLoadLine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenFileDialog dlg = new OpenFileDialog
                {
                    Filter = "Shapefile (*.shp)|*.shp",
                    Title = "选择路网(线) Shapefile",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                };

                if (dlg.ShowDialog() == true)
                {
                    mapManager.LoadLineShapefile(dlg.FileName);
                    UpdateMapDisplay();

                    var mapBounds = mapManager.GetMapBounds();
                    coordinateTransformer = new CoordinateTransformer(mapBounds,
                        new System.Windows.Size(MapImage.ActualWidth, MapImage.ActualHeight));

                    MessageBox.Show($"路网加载成功，共 {mapManager.Lines.Count} 条道路");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"路网加载失败: {ex.Message}");
            }
        }

        // 地图缩放与重置按钮
        private void ZoomIn_Click(object sender, RoutedEventArgs e) { mapManager.ZoomIn(); UpdateMapDisplay(); }
        private void ZoomOut_Click(object sender, RoutedEventArgs e) { mapManager.ZoomOut(); UpdateMapDisplay(); }
        private void ResetView_Click(object sender, RoutedEventArgs e) { mapManager.ResetView(); UpdateMapDisplay(); }

        // 全景切换按钮
        private void BtnPrev_Click(object sender, RoutedEventArgs e) => NavigatePanorama(-1);
        private void BtnNext_Click(object sender, RoutedEventArgs e) => NavigatePanorama(1);

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Left) NavigatePanorama(-1);
            if (e.Key == Key.Right) NavigatePanorama(1);
        }

        #endregion

        #region 7. 辅助方法 (Helpers)

        /// <summary>
        /// 通用的全景导航逻辑 (前一张/后一张)
        /// 自动跳过没有图片的节点
        /// </summary>
        private void NavigatePanorama(int direction)
        {
            if (streetNodes.Count == 0) return;

            int newIndex = currentIndex;
            // 循环查找下一个有图片的节点，防止死循环限制次数为总数
            for (int i = 0; i < streetNodes.Count; i++)
            {
                newIndex = (newIndex + direction + streetNodes.Count) % streetNodes.Count;
                if (!string.IsNullOrEmpty(streetNodes[newIndex].ImagePath) &&
                    File.Exists(streetNodes[newIndex].ImagePath))
                {
                    LoadPanorama(newIndex);
                    return;
                }
            }
        }

        private StreetNode FindNearestNodeWithImage(double lon, double lat)
        {
            return streetNodes
                .Where(n => !string.IsNullOrEmpty(n.ImagePath) && File.Exists(n.ImagePath))
                .OrderBy(n => CalculateDistance(n.Lon, n.Lat, lon, lat))
                .FirstOrDefault();
        }

        private double CalculateDistance(double x1, double y1, double x2, double y2)
        {
            return Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
        }

        private BitmapImage BitmapToImageSource(Bitmap bitmap)
        {
            using (var memory = new MemoryStream())
            {
                bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
                memory.Position = 0;
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memory;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                return bitmapImage;
            }
        }

        #endregion
    }
}