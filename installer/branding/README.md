# 统一视觉资源

原创几何音频柱 + 双向转换环，深蓝黑底、青蓝紫渐变。无外部图片或字体文件随包分发。

- App、Windows EXE、桌面/开始菜单、已安装应用列表、安装与卸载程序：同源 PNG / ICO。
- macOS app bundle：同源 ICNS；需要在原生 macOS 打包验证。
- Windows 安装/卸载：Inno Setup **6.7+** 的 `modern dark polar includetitlebar hidebevels`，保留原生键盘操作和高对比度回退。
- 欢迎/完成页使用品牌侧图；中间步骤使用小图标。主要安装、卸载流程中文化，未覆盖的底层系统错误使用编译器默认英文。

开发者重建：`python scripts/render-branding.py`（Pillow + NumPy；Windows Segoe UI 字体绘制侧图英文）。生成结果已保存在项目中，用户运行或正常打包不依赖 Python。

不修改 AppId、版本号、默认安装路径、数据清理范围，不增加自启动、联网、强制启动或文件关联。
