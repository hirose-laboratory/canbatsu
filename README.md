# CAN伐 - ARとAIを用いたスマート林業推進システム
第37回 全国高等専門学校プログラミングコンテスト 課題部門 応募作品

## ソースコード
このリポジトリはUnityアプリのソースコードを管理しています。  
[サーバー側の処理 (森林簿データの取り込み・3Dマップ作成)のリポジトリはこちらから](https://github.com/hirose-laboratory/canbatsu-backend)  

## システム概要
CAN伐は、スマート林業を支援します。

## XREAL SDK について
本リポジトリには XREAL SDK を含めていません (再配布しないため)。
ビルドするには以下の手順で各自用意してください。

1. XREAL Developer (https://developer.xreal.com/download/) で規約に同意し、
   XREAL SDK 3.1.0 (com.xreal.xr) をダウンロード
2. 展開して `Packages/com.xreal.xr` に配置
3. 同梱の AAR ファイル間でパッケージ名 `nrsdk.pack` が重複しており Gradle ビルドが失敗するため、
   各 AAR の名前空間を個別の名前に変更する 
