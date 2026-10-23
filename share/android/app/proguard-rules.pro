# kotlinx.serialization: keep generated serializers
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.**
-keepclassmembers class kotlinx.serialization.json.** { *** Companion; }
-keepclasseswithmembers class kotlinx.serialization.json.** { kotlinx.serialization.KSerializer serializer(...); }
-keep,includedescriptorclasses class com.clipfx.share.**$$serializer { *; }
-keepclassmembers class com.clipfx.share.** { *** Companion; }
-keepclasseswithmembers class com.clipfx.share.** { kotlinx.serialization.KSerializer serializer(...); }
-dontwarn com.google.zxing.**
-keep class com.journeyapps.barcodescanner.** { *; }
