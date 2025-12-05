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
using System.Drawing;
using Point = System.Windows.Point;
using HelixToolkit.Geometry;
using System.Windows.Controls;
using Microsoft.Win32;
using static LocalStreetViewApp.MapManager;

namespace LocalStreetViewApp
{
    public partial class MainWindow : Window
    {
        private double rotationY = 0.0;
        private double rotationX = 0.0;
        private bool isDragging = false;
        private Point lastMousePos;

        private PerspectiveCamera camera;
        private ModelVisual3D sphereModelVisual;
        private GeometryModel3D sphereGeometry;
        private bool isMapPanning = false;
        private Point mapPanStartPoint;

        // 使用 StreetNode 替代 PanoramaLocation
        private List<StreetNode> streetNodes = new List<StreetNode>();
        private int currentIndex = 0;

        // 地图相关变量
        private MapManager mapManager;
        private CoordinateTransformer coordinateTransformer;
        private GeometryModel3D compassGeometry;
        public MainWindow()
        {
            InitializeComponent();
            SetupViewport();
            InitializeMap();
            AttachMouseHandlers();
            this.KeyDown += MainWindow_KeyDown;
            MapImage.SizeChanged += (s, e) => UpdateMapDisplay();
        }
        private void MapImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            isMapPanning = true;
            mapPanStartPoint = e.GetPosition(MapImage);
            // 捕获鼠标，防止拖出图片区域后松开鼠标导致状态未重置
            MapImage.CaptureMouse();

            // 改变鼠标样式提示用户
            MapImage.Cursor = Cursors.SizeAll;
        }
        private void InitializeMap()
        {
            try
            {
                mapManager = new MapManager();
                // 初始渲染地图
                UpdateMapDisplay();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"地图初始化失败: {ex.Message}");
            }
        }

        private void UpdateMapDisplay()
        {
            try
            {
                if (mapManager == null) return;

                int w = (int)MapImage.ActualWidth;
                int h = (int)MapImage.ActualHeight;
                if (w <= 0 || h <= 0) { w = 800; h = 300; }

                // ★ 获取当前高亮的点 ID
                int currentId = -1;
                if (streetNodes.Count > 0 && currentIndex >= 0 && currentIndex < streetNodes.Count)
                {
                    currentId = streetNodes[currentIndex].Id;
                }

                // ★ 传入 currentId 进行渲染
                using (var mapBitmap = mapManager.RenderMap(streetNodes, w, h, currentId))
                {
                    MapImage.Source = BitmapToImageSource(mapBitmap);
                }

                // 更新坐标转换器
                var currentBounds = mapManager.GetCurrentViewBounds();
                coordinateTransformer = new CoordinateTransformer(currentBounds, new System.Windows.Size(w, h));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"更新地图显示失败: {ex.Message}");
            }
        }
        private void CreateCompass()
        {
            var meshBuilder = new MeshBuilder();

            // 定义四个点 (顺时针或逆时针，确保面朝上)
            // 这里创建一个 XZ 平面上的板子
            double size = 8.0; // 罗盘大小
            double yPos = -5.0; // 高度（负数表示在脚下）

            // 添加一个四边形 (p0, p1, p2, p3)
            meshBuilder.AddQuad(
       new System.Numerics.Vector3((float)-size, (float)yPos, (float)size),  // 左下
       new System.Numerics.Vector3((float)size, (float)yPos, (float)size),   // 右下
       new System.Numerics.Vector3((float)size, (float)yPos, (float)-size),  // 右上
       new System.Numerics.Vector3((float)-size, (float)yPos, (float)-size)  // 左上
   );

            // 2. 创建材质：使用 XAML 中的资源
            var compassGrid = (Grid)this.FindResource("CompassDesign");
            var visualBrush = new VisualBrush(compassGrid);

            // 关键：解决 VisualBrush 在 3D 中可能的渲染问题
            RenderOptions.SetCachingHint(visualBrush, CachingHint.Cache);

            var material = new DiffuseMaterial(visualBrush);

            compassGeometry = new GeometryModel3D(meshBuilder.ToMesh().ToWndMeshGeometry3D(), material);

            // 解决背面透明问题（让罗盘两面都能看到，防止旋转时消失）
            compassGeometry.BackMaterial = material;

            // 4. 添加到视口
            // 之前 sphereModelVisual 只是用来放球体，现在我们把它当容器，或者直接加进去
            // 这里我们把罗盘也加到 sphereModelVisual 中，方便管理
            if (sphereModelVisual.Content is Model3DGroup group)
            {
                group.Children.Add(compassGeometry);
            }
            else
            {
                // 如果 Content 之前是单个 GeometryModel3D，现在升级为 Group
                var newGroup = new Model3DGroup();
                if (sphereModelVisual.Content != null)
                    newGroup.Children.Add(sphereModelVisual.Content); // 把原来的球加进去

                newGroup.Children.Add(compassGeometry); // 把罗盘加进去
                sphereModelVisual.Content = newGroup;
            }
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
        private void BtnLoadLine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenFileDialog dlg = new OpenFileDialog();
                dlg.Filter = "Shapefile (*.shp)|*.shp";
                dlg.Title = "选择路网(线) Shapefile";
                // 这里可以设置你的默认路径
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                //dlg.InitialDirectory = @"C:\Users\MECHREVO\Desktop\A";
                if (dlg.ShowDialog() == true)
                {
                    // 1. 调用 MapManager 加载线数据
                    mapManager.LoadLineShapefile(dlg.FileName);

                    // 2. 重新渲染地图（RenderMap 现在会自动画出线和点）
                    UpdateMapDisplay();

                    // 3. 重新计算坐标转换器（因为地图范围可能因为加了线而变大）
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
        // 加载SHP文件并绑定图片
        private void BtnLoadSHP_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenFileDialog dlg = new OpenFileDialog();
                dlg.Filter = "Shapefile (*.shp)|*.shp";
                dlg.InitialDirectory = @"C:\Users\MECHREVO\Desktop\A";

                if (dlg.ShowDialog() == true)
                {
                    // 加载SHP文件
                    mapManager.LoadShapefile(dlg.FileName);
                    streetNodes = mapManager.Nodes;

                    // 自动绑定图片
                    string imageFolder = @"C:\Users\24462\Desktop\images";

                    // 如果指定路径不存在，尝试相对路径
                    if (!Directory.Exists(imageFolder))
                    {
                        imageFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
                        Console.WriteLine($"使用相对图片路径: {imageFolder}");
                    }

                    int boundCount = mapManager.AutoBindImages(imageFolder);

                    // 更新地图显示
                    UpdateMapDisplay();

                    // 初始化坐标转换器
                    var mapBounds = mapManager.GetMapBounds();
                    coordinateTransformer = new CoordinateTransformer(mapBounds,
                        new System.Windows.Size(MapImage.ActualWidth, MapImage.ActualHeight));

                    string message = $"读取完毕，共 {streetNodes.Count} 个点";
                    if (boundCount > 0)
                    {
                        message += $"，成功绑定 {boundCount} 张图片";

                        // 如果有绑定图片的节点，加载第一个
                        var firstNodeWithImage = streetNodes.FirstOrDefault(n => !string.IsNullOrEmpty(n.ImagePath) && File.Exists(n.ImagePath));
                        if (firstNodeWithImage != null)
                        {
                            currentIndex = streetNodes.IndexOf(firstNodeWithImage);
                            LoadPanorama(currentIndex);
                        }
                    }
                    else
                    {
                        message += "，但没有成功绑定任何图片";
                    }
                    MessageBox.Show(message);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载SHP文件失败: {ex.Message}");
                Console.WriteLine($"异常详情: {ex.StackTrace}");
            }
        }

        private System.Windows.Media.Media3D.MeshGeometry3D CreateSphereMesh(double radius, int thetaDiv, int phiDiv)
        {
            var mesh = new System.Windows.Media.Media3D.MeshGeometry3D();

            double dt = Math.PI / thetaDiv;
            double dp = 2 * Math.PI / phiDiv;

            // 创建顶点
            for (int pi = 0; pi <= thetaDiv; pi++)
            {
                double theta = pi * dt;
                for (int ti = 0; ti <= phiDiv; ti++)
                {
                    double phi = ti * dp;

                    double x = radius * Math.Sin(theta) * Math.Cos(phi);
                    double y = radius * Math.Cos(theta);
                    double z = radius * Math.Sin(theta) * Math.Sin(phi);

                    mesh.Positions.Add(new System.Windows.Media.Media3D.Point3D(x, y, z));
                    mesh.TextureCoordinates.Add(new System.Windows.Point(ti / (double)phiDiv, pi / (double)thetaDiv));
                    mesh.Normals.Add(new System.Windows.Media.Media3D.Vector3D(x, y, z));
                }
            }

            // 创建三角形
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

        private void LoadPanorama(int index)
        {
            if (streetNodes.Count == 0 || index < 0 || index >= streetNodes.Count)
            {
                Console.WriteLine($"无法加载全景: 索引 {index} 超出范围或节点列表为空");
                return;
            }

            var node = streetNodes[index];
            Console.WriteLine($"尝试加载全景: 节点 {index}, 图片路径: {node.ImagePath}");

            if (string.IsNullOrEmpty(node.ImagePath))
            {
                MessageBox.Show("该节点没有绑定的图片文件");
                Console.WriteLine($"节点 {index} 没有图片路径");
                return;
            }

            if (!File.Exists(node.ImagePath))
            {
                MessageBox.Show("图片文件不存在: " + node.ImagePath);
                Console.WriteLine($"图片文件不存在: {node.ImagePath}");
                return;
            }

            currentIndex = index;

            try
            {
                // 读取图片
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(node.ImagePath, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                var imgBrush = new ImageBrush(bmp)
                {
                    Stretch = Stretch.Fill,
                    ViewportUnits = BrushMappingMode.Absolute
                };
                RenderOptions.SetBitmapScalingMode(imgBrush, BitmapScalingMode.HighQuality);

                var material = new EmissiveMaterial(imgBrush);

                // 使用完全限定的类型名称
                var mesh = CreateSphereMesh(10, 64, 32);

                sphereGeometry = new System.Windows.Media.Media3D.GeometryModel3D
                {
                    Geometry = mesh,
                    Material = material,
                    BackMaterial = material
                };

                UpdateSphereRotation();
                sphereModelVisual.Content = sphereGeometry;

                string fileName = Path.GetFileName(node.ImagePath);
                txtInfo.Text = $"{currentIndex + 1}/{streetNodes.Count} - {fileName} ({bmp.PixelWidth}×{bmp.PixelHeight}) - 距离: {node.DistanceToImage:F2}米";
                UpdateMapDisplay();
                // 更新地图位置标记

                Console.WriteLine($"成功加载全景图片: {fileName}");
                if (sphereGeometry == null)
                {
                    sphereGeometry = new GeometryModel3D
                    {
                        Geometry = mesh,
                        Material = material,
                        BackMaterial = material
                    };
                }
                else
                {
                    // 如果对象已存在，直接替换材质和几何体，避免重建 Group
                    sphereGeometry.Geometry = mesh;
                    sphereGeometry.Material = material;
                    sphereGeometry.BackMaterial = material;
                }

                var group = new Model3DGroup();
                group.Children.Add(sphereGeometry);

                // 设置到 Visual
                sphereModelVisual.Content = group;

                CreateCompass();

                UpdateSphereRotation();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载图片失败: {ex.Message}");
                Console.WriteLine($"加载图片异常: {ex.Message}");
                Console.WriteLine($"堆栈跟踪: {ex.StackTrace}");
            }
        }

        private void MapImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // 1. 只有左键点击才处理
            if (e.ChangedButton != MouseButton.Left) return;

            if (streetNodes.Count == 0 || coordinateTransformer == null) return;

            // 获取屏幕点击坐标
            var clickPoint = e.GetPosition(MapImage);

            // 获取点击位置对应的地理坐标
            var (geoX, geoY) = coordinateTransformer.ScreenToGeo(clickPoint);

            // 查找地理位置最近的候选节点
            var candidateNode = FindNearestNodeWithImage(geoX, geoY);

            if (candidateNode != null)
            {
                // ★★★ 核心修改：增加距离验证 ★★★

                // 将候选节点的地理坐标转回屏幕坐标
                var nodeScreenPos = coordinateTransformer.GeoToScreen(candidateNode.Lon, candidateNode.Lat);

                // 计算鼠标点击点与该节点在屏幕上的像素距离
                double dx = clickPoint.X - nodeScreenPos.X;
                double dy = clickPoint.Y - nodeScreenPos.Y;
                double pixelDistance = Math.Sqrt(dx * dx + dy * dy);

                // 设定阈值：例如 15 像素（大约是一个手指点击或鼠标光标的容错范围）
                double hitThreshold = 15.0;

                if (pixelDistance <= hitThreshold)
                {
                    // 距离足够近，确认为选中
                    int newIndex = streetNodes.IndexOf(candidateNode);

                    // 只有当点击的不是当前点时才加载
                    if (newIndex != currentIndex)
                    {
                        currentIndex = newIndex;
                        LoadPanorama(currentIndex);
                    }
                }
                else
                {
                    Console.WriteLine($"点击未命中: 距离最近点 {pixelDistance:F1} 像素 (阈值 {hitThreshold})");
                    // 距离太远，视为点击了空白处，不做任何操作
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

        private void MapImage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (mapManager == null) return;

            // 获取鼠标位置
            var mousePos = e.GetPosition(MapImage);
            mapManager.ZoomAtPoint(e.Delta, mousePos.X, mousePos.Y, MapImage.ActualWidth, MapImage.ActualHeight);

            // 重新渲染地图
            UpdateMapDisplay();

            // 阻止事件冒泡（可选，防止外层容器也跟着滚动）
            e.Handled = true;
        }

        private void MapImage_MouseMove(object sender, MouseEventArgs e)
        {
            // 1. 处理拖拽逻辑
            if (isMapPanning && mapManager != null)
            {
                var currentPoint = e.GetPosition(MapImage);

                // 计算位移
                double dx = currentPoint.X - mapPanStartPoint.X;
                double dy = currentPoint.Y - mapPanStartPoint.Y;

                // 如果位移太小（防抖），不处理
                if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1)
                {
                    // 调用 MapManager 进行平移
                    mapManager.Pan(dx, dy, MapImage.ActualWidth, MapImage.ActualHeight);

                    // 重新渲染地图
                    UpdateMapDisplay();

                    // 更新起始点，以便下一次计算增量
                    mapPanStartPoint = currentPoint;
                }
            }

            // 2. 原有的显示坐标逻辑
            if (coordinateTransformer != null)
            {
                var mousePoint = e.GetPosition(MapImage);
                var (x, y) = coordinateTransformer.ScreenToGeo(mousePoint);
                MapCoordText.Text = $"坐标: ({x:F6}, {y:F6})";
            }
        }

        // 更新位置标记
        private void MapImage_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (isMapPanning)
            {
                isMapPanning = false;
                MapImage.ReleaseMouseCapture();
                MapImage.Cursor = Cursors.Arrow; // 恢复鼠标样式
            }
        }

        // 地图缩放控制
        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            mapManager.ZoomIn();
            UpdateMapDisplay();
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            mapManager.ZoomOut();
            UpdateMapDisplay();
        }

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            mapManager.ResetView();
            UpdateMapDisplay();
        }

        // 全景浏览控制
        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            if (streetNodes.Count == 0) return;

            // 查找前一个有图片的节点
            int newIndex = currentIndex;
            for (int i = 1; i <= streetNodes.Count; i++)
            {
                newIndex = (currentIndex - i + streetNodes.Count) % streetNodes.Count;
                if (!string.IsNullOrEmpty(streetNodes[newIndex].ImagePath) && File.Exists(streetNodes[newIndex].ImagePath))
                    break;
            }

            if (newIndex != currentIndex)
            {
                LoadPanorama(newIndex);
            }
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (streetNodes.Count == 0) return;

            // 查找后一个有图片的节点
            int newIndex = currentIndex;
            for (int i = 1; i <= streetNodes.Count; i++)
            {
                newIndex = (currentIndex + i) % streetNodes.Count;
                if (!string.IsNullOrEmpty(streetNodes[newIndex].ImagePath) && File.Exists(streetNodes[newIndex].ImagePath))
                    break;
            }

            if (newIndex != currentIndex)
            {
                LoadPanorama(newIndex);
            }
        }

        // 原有的3D视图方法保持不变
        private void SetupViewport()
        {
            camera = new PerspectiveCamera
            {
                FieldOfView = 70,
                Position = new Point3D(0, 0, 0.001),
                LookDirection = new Vector3D(0, 0, 1),
                UpDirection = new Vector3D(0, 1, 0)
            };
            helixViewport.Camera = camera;

            helixViewport.Children.Add(new DefaultLights());
            sphereModelVisual = new ModelVisual3D();
            helixViewport.Children.Add(sphereModelVisual);
        }

        private void UpdateSphereRotation()
        {
            // 创建旋转变换
            var transformGroup = new Transform3DGroup();

            // 1. X轴旋转 (上下看)
            transformGroup.Children.Add(new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(1, 0, 0), rotationX)));

            // 2. Y轴旋转 (左右看)
            transformGroup.Children.Add(new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(0, 1, 0), rotationY)));

            // ★★★ 关键：将旋转应用到所有物体 ★★★

            // 应用给球体
            if (sphereGeometry != null)
                sphereGeometry.Transform = transformGroup;

            // 应用给罗盘
            if (compassGeometry != null)
                compassGeometry.Transform = transformGroup;
        }

        private void AttachMouseHandlers()
        {
            helixViewport.MouseDown += HelixViewport_MouseDown;
            helixViewport.MouseMove += HelixViewport_MouseMove;
            helixViewport.MouseUp += HelixViewport_MouseUp;
            helixViewport.MouseWheel += HelixViewport_MouseWheel;
        }

        private void HelixViewport_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                isDragging = true;
                lastMousePos = e.GetPosition(this);
                helixViewport.CaptureMouse();
                Mouse.OverrideCursor = Cursors.SizeAll;
            }
        }

        private void HelixViewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging) return;
            var pos = e.GetPosition(this);
            double dx = pos.X - lastMousePos.X;
            double dy = pos.Y - lastMousePos.Y;
            lastMousePos = pos;

            double rotationSpeed = 0.2;
            rotationY = (rotationY + dx * rotationSpeed) % 360;
            rotationX += dy * rotationSpeed * 0.5;

            rotationX = Math.Max(-90, Math.Min(90, rotationX));
            UpdateSphereRotation();
        }

        private void HelixViewport_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                isDragging = false;
                helixViewport.ReleaseMouseCapture();
                Mouse.OverrideCursor = null;
            }
        }

        private void HelixViewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double delta = e.Delta > 0 ? -2 : 2;
            camera.FieldOfView = Math.Max(20, Math.Min(100, camera.FieldOfView + delta));
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Left) BtnPrev_Click(null, null);
            if (e.Key == Key.Right) BtnNext_Click(null, null);
        }
    }
}